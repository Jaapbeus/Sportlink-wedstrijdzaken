using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Email;
using Planner.Shared.TeamResolution;
using SportlinkFunction.Monitoring;
using Planner.Shared.Email;

namespace SportlinkFunction.Email;

/// <summary>
/// Idempotentiebesluit voor de e-mailverwerking (#712).
///
/// <para>
/// Het bestaan van een rij in <c>planner.EmailVerwerking</c> betekende voorheen "al verwerkt".
/// Dat is fout: de rij wordt aangemaakt vóór de verwerking, dus élke fout daarna (verzendfout,
/// plannerfout, templatefout) liet een rij achter waardoor de volgende poll het bericht oversloeg
/// én als gelezen markeerde. Netto kreeg de afzender nooit antwoord en verdween het bericht uit de
/// wachtrij. Het besluit hangt daarom af van de <b>eindstatus</b>, niet van het bestaan van de rij.
/// </para>
///
/// <para>
/// Puur en zonder afhankelijkheden, zodat elk faalscenario los te testen is.
/// </para>
/// </summary>
internal static class EmailIdempotentie
{
    internal const int MaxPogingen = VerwerkingsBesluitRegels.MaxPogingen;

    internal static VerwerkingsBesluit Bepaal(EmailVerwerkingStand? stand) =>
        stand is null ? VerwerkingsBesluit.NieuweVerwerking :
        VerwerkingsBesluitRegels.Bepaal(stand.AntwoordVerstuurd, stand.Status,
            stand.VerzendPogingOnbeslist, stand.Pogingen);

    internal static EmailVerwerkingStandInfo? NaarStandInfo(EmailVerwerkingStand? stand) =>
        stand is null ? null : new(stand.VerwerkingId, stand.Status, stand.Pogingen, Bepaal(stand));
}

public partial class EmailProcessorFunction
{
    // Uitsluitingslijst: geladen vóór elke AI-classificatie (fail-closed bij cold start). (#423, #709)
    private static readonly UitsluitingslijstCache _uitsluitingslijst = new();

    [Function("ProcessIncomingEmails")]
    public async Task Run(
        [TimerTrigger("%EMAIL_POLL_SCHEDULE%")] TimerInfo timer,
        FunctionContext context)
    {
        var log = context.GetLogger("ProcessIncomingEmails");

        // De metadata-check raakt geen berichtinhoud of database. Alleen werk in de inbox
        // wekt de database; vóór volledige Graph-ophaal/AI/mutaties is één verse clubsnapshot vereist.
        var graphService = context.InstanceServices.GetService<IEmailGraphService>();
        var noodmail = graphService is null ? null : EmailPollNoodmailCallbacksFactory.Maak(
            graphService, context.InstanceServices.GetRequiredService<INoodmailThrottleStore>(), EmailVerwerkerPoort.Teksten,
            log, "Ongelezen inboxitems (metadata)");
        var instellingen = await EmailPollVoorbereiding.LeesInstellingenAsync(
            graphService is null ? null : graphService.TelOngelezenInboxBerichtenAsync,
            () => SystemUtilities.WaitForDatabaseConnectionAsync(log),
            () => SystemUtilities.AppSettings.LastLoadFailed,
            () => SystemUtilities.AppSettings.LoadSettingsAsync(log),
            () => EmailVerwerkingsInstellingenOpslag.LeesAsync(
                SystemUtilities.DatabaseConfig.ConnectionString, SystemUtilities.AppSettings.RequireClubCode(), log),
            log, noodmail?.BijVerbindingsfout, noodmail?.BijHerstel);
        if (graphService is null || instellingen is null) return;

        var loggerFactory = context.InstanceServices.GetRequiredService<ILoggerFactory>();
        var teamResolver = context.InstanceServices.GetService<ITeamResolver>();
        if (teamResolver is null)
        {
            log.LogError("Teamresolutie niet beschikbaar (ITeamResolver niet geregistreerd) — verwerking overgeslagen");
            return;
        }

        var poort = new EmailVerwerkerPoort(
            log,
            context.InstanceServices,
            loggerFactory,
            graphService,
            teamResolver,
            context.InstanceServices.GetRequiredService<IEmailPersistenceService>(),
            context.InstanceServices.GetRequiredService<INoodmailThrottleStore>(),
            instellingen.Instellingen);

        // Fase 1 (licht, geen database) en fase 2 (zwaar) staan tier-onafhankelijk in
        // Planner.Endpoints/Email/EmailVerwerkerFasen.cs (#1716 slice 9); de tier-kant in EmailVerwerkerPoort.
        await EmailVerwerkerFasen.VoerUitAsync(poort, _uitsluitingslijst, log);
    }
}
