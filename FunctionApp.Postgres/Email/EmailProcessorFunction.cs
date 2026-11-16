using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.Monitoring;
using FunctionApp.Postgres.Planner;
using Planner.Endpoints.Email;
using Planner.Shared.Email;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/EmailProcessorFunction.cs</c> (#972) —
/// idempotentiebesluit voor de e-mailverwerking. De regelvolgorde staat sinds #1650 eenmalig in
/// <c>Planner.Shared/Email/VerwerkingsBesluit.cs</c>; hier alleen de vertaling van de tier-eigen stand.
/// <para>
/// Het bestaan van een rij in <c>planner.emailverwerking</c> betekende voorheen "al verwerkt".
/// Dat is fout: de rij wordt aangemaakt vóór de verwerking, dus élke fout daarna liet een rij
/// achter waardoor de volgende poll het bericht oversloeg én als gelezen markeerde. Het besluit
/// hangt daarom af van de <b>eindstatus</b>, niet van het bestaan van de rij.
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
    // Uitsluitingslijst: geladen vóór elke AI-classificatie (fail-closed bij cold start).
    private static readonly UitsluitingslijstCache _uitsluitingslijst = new();

    [Function("ProcessIncomingEmails")]
    public async Task Run(
        [TimerTrigger("%EMAIL_POLL_SCHEDULE%")] TimerInfo timer,
        FunctionContext context)
    {
        var log = context.GetLogger("ProcessIncomingEmails");

        // Lees de clubgebonden snapshot vóór Graph of enige mailboxmutatie.
        var instellingen = await EmailTimerInstellingenGate.LeesVoorVerwerkingAsync(async () =>
        {
            await PostgresAppSettings.LoadSettingsAsync(log);
            return await EmailVerwerkingsInstellingenOpslag.LeesAsync(
                PostgresDatabaseConfig.ConnectionString, PostgresClubScope.Primary, log);
        }, () => log.LogWarning("E-mailinstellingen niet beschikbaar; verwerking uit"),
            status => log.LogInformation("Email processor uitgeschakeld voor deze club ({Status})", status));
        if (instellingen is null) return;

        // IEmailGraphService is alleen geregistreerd als Graph geconfigureerd is én
        // EgressGuard.ExternalIntegrationsAllowed() true is (Program.cs, #857) — buiten productie
        // (lokaal, CI) is dit onvoorwaardelijk null. Dat is verwacht, correct gedrag: nooit
        // gooien, gewoon overslaan.
        var graphService = context.InstanceServices.GetService<IEmailGraphService>();
        if (graphService == null)
        {
            log.LogError("GraphServiceClient niet beschikbaar — controleer Graph settings");
            return;
        }

        var poort = new EmailVerwerkerPoort(
            log,
            context.InstanceServices,
            context.InstanceServices.GetRequiredService<ILoggerFactory>(),
            graphService,
            context.InstanceServices.GetRequiredService<INoodmailThrottleStore>(),
            PostgresDatabaseConfig.ConnectionString,
            instellingen.Instellingen);

        // Fase 1 (licht, geen database) en fase 2 (zwaar) staan tier-onafhankelijk in
        // Planner.Endpoints/Email/EmailVerwerkerFasen.cs (#1716 slice 9); de tier-kant in EmailVerwerkerPoort.
        await EmailVerwerkerFasen.VoerUitAsync(poort, _uitsluitingslijst, log);
    }
}
