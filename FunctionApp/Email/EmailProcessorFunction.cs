using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.TeamResolution;
using SportlinkFunction.Monitoring;
using SportlinkFunction.Processing;
using Planner.Shared;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;

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
    // Throttle-sleutels voor INoodmailThrottleStore (#831). Vóór #831 stond deze staat in een
    // static/volatile veld op deze klasse (procesgeheugen) — dat reset bij een cold start van de
    // Consumption-plan-worker, waardoor het throttle-gedrag afhing van iets wat de code zelf niet
    // controleert: hoe vaak de host in de praktijk herstart. Zie INoodmailThrottleStore voor de
    // volledige onderbouwing en DatabaseUitvalMonitorFunction voor de sleutel die hiermee gedeeld wordt.
    internal const string DatabaseNoodmailSleutel = DatabaseUitvalMonitorFunction.ThrottleSleutel;
    internal const string OpenAiQuotaNoodmailSleutel = "openai-quota-noodmail";
    private static readonly TimeSpan OpenAiQuotaNoodmailInterval = TimeSpan.FromHours(24);

    // Uitsluitingslijst: geladen vóór elke AI-classificatie (fail-closed bij cold start). (#423, #709)
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
        var ctx = new EmailRunContext(
            log,
            context.InstanceServices,
            loggerFactory,
            graphService,
            teamResolver,
            context.InstanceServices.GetRequiredService<IEmailPersistenceService>(),
            context.InstanceServices.GetRequiredService<INoodmailThrottleStore>(),
            new EmailBatchFilterService(),
            new EmailReplyPolicyService());

        // Fase 1 (licht, geen database) en fase 2 (zwaar) staan in EmailProcessorFunction.Fasen.cs.
        var licht = await VoerFase1LichtAsync(ctx);
        if (licht is null)
            return;

        await VoerFase2ZwaarAsync(ctx, licht);
    }

    /// <summary>
    /// Zorgt dat de uitsluitingslijst niet verouderd is en filtert de batch ermee — vóór er één
    /// bericht naar de AI-provider gaat (#709). Retourneert <c>null</c> als er niet geclassificeerd
    /// mag worden omdat de lijst nooit geladen is en ook nu niet te laden is (fail-closed, #423).
    /// </summary>
    internal static async Task<List<InkomendBericht>?> FilterMetVerseUitsluitingslijstAsync(
        List<InkomendBericht> teClassificeren,
        UitsluitingslijstCache uitsluitingslijst,
        Func<Task<HashSet<string>>> laadLijstAsync,
        EmailBatchFilterService batchFilterService,
        DateTime nuUtc,
        ILogger log)
    {
        var stand = await uitsluitingslijst.VerversIndienVerouderdAsync(laadLijstAsync, nuUtc, log);

        return stand switch
        {
            UitsluitingslijstStand.Ontbreekt => null,
            // Alleen na een verse lijst kan de uitkomst van het voorfilter achterhaald zijn.
            UitsluitingslijstStand.Ververst =>
                batchFilterService.FilterUitgeslotenAdressen(teClassificeren, uitsluitingslijst.Adressen),
            _ => teClassificeren
        };
    }
}
