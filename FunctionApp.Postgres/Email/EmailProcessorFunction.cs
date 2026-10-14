using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.Monitoring;
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
    /// <summary>Maximaal aantal verwerkingspogingen per bericht; de waarde en onderbouwing staan in <see cref="VerwerkingsBesluitRegels.MaxPogingen"/> (#1650).</summary>
    internal const int MaxPogingen = VerwerkingsBesluitRegels.MaxPogingen;

    /// <summary>Statussen waarna een bericht niet opnieuw verwerkt mag worden.</summary>
    private static readonly EmailStatus[] DefinitieveStatussen =
    [
        EmailStatus.AntwoordVerstuurd,
        EmailStatus.GeenAntwoordNodig,
        EmailStatus.BuitenScope
    ];

    /// <summary>
    /// Is deze verwerking definitief afgerond? <c>AntwoordVerstuurd</c> is hier leidend: dat wordt
    /// uitsluitend vastgelegd nádat een antwoord echt verstuurd is, dus een gezette waarde sluit een
    /// tweede antwoord uit — ook als de status daarna nog op 'Fout' is gezet of onbekend is.
    /// </summary>
    internal static bool IsDefinitief(EmailVerwerkingStand stand)
        => stand.AntwoordVerstuurd
           || (Enum.TryParse<EmailStatus>(stand.Status, out var status)
               && DefinitieveStatussen.Contains(status));

    /// <summary>De volgorde van de regels staat in <see cref="VerwerkingsBesluitRegels"/> (#1650).</summary>
    internal static VerwerkingsBesluit Bepaal(EmailVerwerkingStand? stand)
        => stand is null
            ? VerwerkingsBesluit.NieuweVerwerking
            : VerwerkingsBesluitRegels.BepaalVoorBestaandeRij(IsDefinitief(stand), stand.VerzendPogingOnbeslist, stand.Pogingen);

    /// <summary>Typevrije stand voor <see cref="EmailVerwerkingsAfhandeling"/> (#1650), inclusief besluit.</summary>
    internal static EmailVerwerkingStandInfo? NaarStandInfo(EmailVerwerkingStand? stand)
        => stand is null ? null : new(stand.VerwerkingId, stand.Status, stand.Pogingen, Bepaal(stand));
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

        if (!string.Equals(Environment.GetEnvironmentVariable("EmailProcessorEnabled"),
                "true", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation("Email processor uitgeschakeld");
            return;
        }

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
            PostgresDatabaseConfig.ConnectionString);

        // Fase 1 (licht, geen database) en fase 2 (zwaar) staan tier-onafhankelijk in
        // Planner.Endpoints/Email/EmailVerwerkerFasen.cs (#1716 slice 9); de tier-kant in EmailVerwerkerPoort.
        await EmailVerwerkerFasen.VoerUitAsync(poort, _uitsluitingslijst, log);
    }
}
