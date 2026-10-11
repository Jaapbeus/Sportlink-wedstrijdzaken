using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using FunctionApp.Postgres.Monitoring;
using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Processing;
using FunctionApp.Postgres.TeamResolution;
using Planner.Shared;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;

namespace FunctionApp.Postgres.Email;

/// <summary>Wat er met een inkomend bericht moet gebeuren op basis van een eerdere verwerking.</summary>
internal enum VerwerkingsBesluit
{
    /// <summary>Nog niet eerder gezien — nieuwe verwerkingsrij aanmaken.</summary>
    NieuweVerwerking,

    /// <summary>Eerdere poging is niet afgerond — bestaande rij hergebruiken en opnieuw verwerken.</summary>
    HerhaalVerwerking,

    /// <summary>Definitief afgehandeld — niets meer doen (en dus zeker niet opnieuw antwoorden).</summary>
    OverslaanAlAfgerond,

    /// <summary>Te vaak mislukt — opgeven zodat het bericht de wachtrij niet blijft blokkeren.</summary>
    OpgevenNaMaxPogingen,

    /// <summary>
    /// Er is een verzendpoging vastgelegd waarvan de uitkomst onbekend is. Niet opnieuw
    /// versturen — het eerste antwoord kan de deur al uit zijn — maar ter beoordeling neerleggen.
    /// </summary>
    OnbeslistNaVerzendPoging
}

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/EmailProcessorFunction.cs</c> (#972) —
/// idempotentiebesluit voor de e-mailverwerking. Woordelijke kopie: puur en zonder
/// afhankelijkheden, zodat elk faalscenario los te testen is.
/// <para>
/// Het bestaan van een rij in <c>planner.emailverwerking</c> betekende voorheen "al verwerkt".
/// Dat is fout: de rij wordt aangemaakt vóór de verwerking, dus élke fout daarna liet een rij
/// achter waardoor de volgende poll het bericht oversloeg én als gelezen markeerde. Het besluit
/// hangt daarom af van de <b>eindstatus</b>, niet van het bestaan van de rij.
/// </para>
/// </summary>
internal static class EmailIdempotentie
{
    /// <summary>
    /// Maximaal aantal verwerkingspogingen per bericht. Drie is genoeg om tijdelijke fouten (Graph
    /// 429/503, een net herstelde database, een time-out) uit te zitten, en laag genoeg dat een
    /// structurele fout de wachtrij niet lang bezet houdt: de poll pakt de 10 oudste ongelezen
    /// berichten, dus tien blijvend falende berichten zouden anders alle nieuwe post tegenhouden.
    /// </summary>
    internal const int MaxPogingen = 3;

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

    internal static VerwerkingsBesluit Bepaal(EmailVerwerkingStand? stand)
    {
        if (stand is null)
            return VerwerkingsBesluit.NieuweVerwerking;

        if (IsDefinitief(stand))
            return VerwerkingsBesluit.OverslaanAlAfgerond;

        // Vóór de pogingengrens: een onbekende verzenduitkomst is geen "mislukte poging" die je nog een
        // keer mag proberen. Zou dit ná de grens staan, dan zou een bericht met twee eerdere pogingen
        // alsnog opnieuw verstuurd worden.
        if (stand.VerzendPogingOnbeslist)
            return VerwerkingsBesluit.OnbeslistNaVerzendPoging;

        return stand.Pogingen >= MaxPogingen
            ? VerwerkingsBesluit.OpgevenNaMaxPogingen
            : VerwerkingsBesluit.HerhaalVerwerking;
    }
}

public partial class EmailProcessorFunction
{
    // Throttle-sleutels voor INoodmailThrottleStore. Geen gedeelde sleutel met een database-
    // uitvalmonitor op deze tier (zie klassekop, item 5) — de literal staat daarom hier vast in
    // plaats van naar zo'n monitorklasse te verwijzen.
    internal const string DatabaseNoodmailSleutel = "database-noodmail";
    internal const string OpenAiQuotaNoodmailSleutel = "openai-quota-noodmail";
    private static readonly TimeSpan OpenAiQuotaNoodmailInterval = TimeSpan.FromHours(24);

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

        var ctx = new EmailRunContext(
            log,
            context.InstanceServices,
            context.InstanceServices.GetRequiredService<ILoggerFactory>(),
            graphService,
            context.InstanceServices.GetRequiredService<INoodmailThrottleStore>(),
            PostgresDatabaseConfig.ConnectionString,
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
    /// bericht naar de AI-provider gaat. Retourneert <c>null</c> als er niet geclassificeerd mag
    /// worden omdat de lijst nooit geladen is en ook nu niet te laden is (fail-closed).
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

    private static async Task<HashSet<string>> LaadUitgeslotenAdressenAsync(string cs, string clubCode, ILogger log)
    {
        var adressen = await SqlEmailPersistenceRepository.GetExcludedEmailAddressesAsync(cs, clubCode);
        log.LogInformation("Uitsluitingslijst geladen: {Aantal} adressen", adressen.Count);
        return adressen;
    }
}
