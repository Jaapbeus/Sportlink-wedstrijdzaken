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

        // Lees de clubgebonden snapshot vóór Graph of enige mailboxmutatie. Een ontbrekende
        // migratie, rij of database schakelt de processor uit.
        var instellingen = await EmailTimerInstellingenGate.LeesVoorVerwerkingAsync(async () =>
        {
            await SystemUtilities.AppSettings.LoadSettingsAsync(log);
            var clubCode = SystemUtilities.AppSettings.RequireClubCode();
            return await EmailVerwerkingsInstellingenOpslag.LeesAsync(
                SystemUtilities.DatabaseConfig.ConnectionString, clubCode, log);
        }, () => log.LogWarning("E-mailinstellingen niet beschikbaar; verwerking uit"),
            status => log.LogInformation("Email processor uitgeschakeld voor deze club ({Status})", status));
        if (instellingen is null) return;

        // IEmailGraphService is alleen geregistreerd als Graph geconfigureerd is (Program.cs) — een
        // ontbrekende registratie betekent hier hetzelfde als een eerder ontbrekende GraphServiceClient.
        var graphService = context.InstanceServices.GetService<IEmailGraphService>();
        if (graphService == null)
        {
            log.LogError("GraphServiceClient niet beschikbaar — controleer Graph settings");
            return;
        }

        var loggerFactory = context.InstanceServices.GetRequiredService<ILoggerFactory>();

        // Teamnaam→TeamId-vertaallaag (#692). Sinds #700 is dit het ENIGE pad waarlangs een team wordt
        // herkend — de oude regex-normalisatie bestaat niet meer. Ontbreekt de resolver, dan zou elke
        // mail zonder teamherkenning verwerkt worden; dat is erger dan niet verwerken, dus we stoppen.
        var teamResolver = context.InstanceServices.GetService<ITeamResolver>();
        if (teamResolver is null)
        {
            log.LogError("Teamresolutie niet beschikbaar (ITeamResolver niet geregistreerd) — verwerking overgeslagen");
            return;
        }

        // IEmailPersistenceService is onvoorwaardelijk geregistreerd (#827) — geen eigen `new` meer.
        // Persistente noodmail-throttle (#831) — onvoorwaardelijk geregistreerd, want AzureWebJobsStorage
        // is sowieso vereist voor de Functions-host zelf.
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
