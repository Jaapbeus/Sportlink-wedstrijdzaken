using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using static Planner.Shared.Feedback.FeedbackAi;
using static Planner.Shared.Feedback.FeedbackTekst;

namespace Planner.Shared.Feedback;

// Provider-onafhankelijke kern van de feedbackwidget-API (#129, #1006, #1127), gedeeld tussen
// beide database-tiers (#1130). FunctionApp/Feedback/FeedbackFunction.cs en
// FunctionApp.Postgres/Feedback/FeedbackFunction.cs waren ~99% woordelijk identiek — geen
// SqlConnection/Npgsql-afhankelijkheid, alleen IChatClient (provider-agnostisch, Microsoft.Extensions.AI)
// en de GitHub REST API. Deze klasse bevat daarom alle requestvalidatie, de PII-gates, de
// AI-promptopbouw en de GitHub-issue-payload/-aanroep; elke tier houdt alleen een dunne
// Function-entrypoint over (HTTP-trigger, EasyAuthHelper.RequireAdmin, DI-resolutie van
// IChatClient). Geen ASP.NET Core-afhankelijkheid hier expres: dit blijft een pure klasse zoals
// TeamNaamNormalisatie/VeldResolver/SsrfProtection — de HTTP-vertaling (IActionResult) is aan de
// tier-specifieke entrypoint.
// #1494: de modellen, AI-aanroepen, GitHub-aanroepen, issuebody, tekstcontrole en rate limiter staan
// in eigen bestanden in deze namespace; FeedbackCore blijft de gevel met de publieke API.

/// <summary>
/// Feedback widget API — pure kern (issue #129, #1006, #1127, #1130).
///
/// Validate: valideert of de gebruikersbeschrijving voldoende informatie bevat, geeft gerichte
/// aanvulvragen terug als er gaten zijn.
///
/// Submit: structureert de feedback met AI en bouwt de GitHub-issue-payload; de daadwerkelijke
/// GitHub-aanroep loopt via een door de caller aangeleverde delegate (<see cref="MaakGitHubIssueAsync"/>
/// als standaardimplementatie), zodat tests een fake kunnen injecteren zonder een echte HTTP-aanroep.
/// </summary>
public static class FeedbackCore
{
    // Toegestane waarden voor Type — moet exact overeenkomen met de vaste keuzes in de Blazor-widget
    // (BlazorAdmin/Shared/FeedbackWidget.razor, de radiogroep "Fout"/"Verzoek"/"Vraag"). Vóór #1127
    // accepteerde de server elke string in dit veld, terwijl Type ongefilterd in de AI-prompt wordt
    // geïnterpoleerd (ValideerVolledigheid, StructureerIssue) én VerzamelTeCheckenTekst het niet
    // meenam — een e-mailadres in Type ging zo naar de AI-provider vóór afwijzing.
    public static readonly string[] ToegestaneTypes = ["Fout", "Verzoek", "Vraag"];

    public static bool IsOngeldigType(string? type) => !ToegestaneTypes.Contains(type);

    // ── Validate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Type wordt als allereerste stap tegen de vaste keuzes gevalideerd — vóór de PII-gate en vóór
    /// elke AI-aanroep (#1127). De PII-gate draait daarna en dekt alle velden die de prompt in kan
    /// gaan — niet alleen Beschrijving/Antwoord.
    /// </summary>
    public static async Task<FeedbackValidatieResultaat> ValidateAsync(FeedbackRequest dto, IChatClient chatClient, ILogger log)
    {
        if (IsOngeldigType(dto.Type))
        {
            log.LogWarning("Feedback-validatie geblokkeerd: onbekend Type-veld (vóór enige verwerking)");
            return new FeedbackValidatieResultaat(FeedbackStatus.OngeldigType, OngeldigTypeMelding());
        }

        if (BevatPii(VerzamelTeCheckenTekst(dto)))
        {
            log.LogWarning("Feedback-validatie geblokkeerd: PII gedetecteerd in invoer (vóór AI-aanroep)");
            return new FeedbackValidatieResultaat(FeedbackStatus.PiiGedetecteerd, PiiMelding());
        }

        var result = await ValideerVolledigheid(chatClient, dto, log);
        return new FeedbackValidatieResultaat(FeedbackStatus.Ok, null, result.Volledig, result.Vragen);
    }

    // ── Voorbeeld + Submit ─────────────────────────────────────────────────────

    /// <summary>
    /// Stelt de uiteindelijke titel + body samen en geeft die terug **zonder** iets te publiceren
    /// (#1205). Dit is de stap die de beheerder te zien krijgt vóór hij bewust bevestigt dat zijn
    /// tekst openbaar op GitHub mag.
    ///
    /// Alle controles van <see cref="SubmitAsync"/> draaien hier in dezelfde volgorde: de
    /// type-allowlist, de PII-gate op de verzamelde invoer en de tweede PII-gate op de uiteindelijke
    /// titel/body. Een voorbeeld dat PII bevat wordt dus net zo geweigerd als een publicatie — het
    /// voorbeeld is geen ontsnappingsroute langs de gates heen.
    /// </summary>
    /// <param name="tijdstipUtc">
    /// Zie <see cref="SubmitAsync"/> — alleen bedoeld om het Tijdstip-veld in tests vast te zetten.
    /// </param>
    public static async Task<FeedbackVoorbeeldResultaat> VoorbeeldAsync(
        FeedbackRequest dto,
        IChatClient chatClient,
        ILogger log,
        DateTime? tijdstipUtc = null)
    {
        var voorbereid = await BereidPublicatieVoorAsync(dto, chatClient, log, tijdstipUtc, "Feedback-voorbeeld");
        if (voorbereid.Status != FeedbackStatus.Ok)
            return new FeedbackVoorbeeldResultaat(voorbereid.Status, voorbereid.Foutmelding);

        return new FeedbackVoorbeeldResultaat(
            FeedbackStatus.Ok,
            null,
            voorbereid.Titel,
            voorbereid.Body,
            voorbereid.Structured!.Samenvatting,
            voorbereid.Structured.Acceptatiecriteria);
    }

    /// <summary>
    /// Vóór alles: Type wordt tegen de vaste keuzes gevalideerd (#1127) — vóór enige verwerking of
    /// externe aanroep, dus ook vóór de eerste PII-gate.
    ///
    /// Daarna twee PII-gates, niet één:
    /// 1. Vóór de AI-aanroep — over alle velden die de prompt in kunnen gaan (Type, Context.Pagina/Versie/
    ///    Browser, elke Vraag én Antwoord), niet alleen Beschrijving/Antwoord zoals de oorspronkelijke
    ///    #427-gate.
    /// 2. Vlak vóór de GitHub-write — over de daadwerkelijke, uiteindelijke titel + body, dus inclusief
    ///    AI-gegenereerde Samenvatting/acceptatiecriteria. AI-output wordt nooit impliciet vertrouwd als
    ///    publiceerbare tekst.
    /// Een blocked input doet daarom nooit een AI-aanroep; een blocked output doet nooit een GitHub-aanroep.
    ///
    /// Staat <see cref="FeedbackRequest.Bevestiging"/> gevuld, dan zijn dat de door de AI geproduceerde
    /// velden zoals de beheerder ze in de voorbeeldstap heeft gezien, en wordt de AI niet opnieuw
    /// aangeroepen (#1205).
    /// </summary>
    /// <param name="tijdstipUtc">
    /// Optioneel vast tijdstip voor de Tijdstip-regel in de body; standaard <c>DateTime.UtcNow</c>.
    /// Uitsluitend bedoeld om die ene regel in tests deterministisch te maken — in productie blijft
    /// het het publicatiemoment, en dat is dan ook het enige verschil tussen een voorbeeld en de
    /// publicatie erna. Die regel is servermetadata, geen tekst van de beheerder.
    /// </param>
    public static async Task<FeedbackSubmitResultaat> SubmitAsync(
        FeedbackRequest dto,
        IChatClient chatClient,
        Func<string, string, string[], Task<(int nummer, string url)>> maakGitHubIssueAsync,
        ILogger log,
        DateTime? tijdstipUtc = null)
    {
        var voorbereid = await BereidPublicatieVoorAsync(dto, chatClient, log, tijdstipUtc, "Feedback");
        if (voorbereid.Status != FeedbackStatus.Ok)
            return new FeedbackSubmitResultaat(voorbereid.Status, voorbereid.Foutmelding);

        var labels = KiesLabels(dto.Type);
        var (issueNummer, issueUrl) = await maakGitHubIssueAsync(voorbereid.Titel!, voorbereid.Body!, labels);

        return new FeedbackSubmitResultaat(FeedbackStatus.Ok, null, issueNummer, issueUrl);
    }

    /// <summary>
    /// De volledig voorbereide, PII-gecontroleerde melding (#764): titel en body zoals ze naar GitHub
    /// zouden gaan. Het endpoint bewaart dit en publiceert het direct (beheerder) of na een klik van
    /// een beheerder (gewone gebruiker) — zonder de AI nogmaals aan te roepen.
    /// </summary>
    public sealed record FeedbackVoorbereiding(FeedbackStatus Status, string? Foutmelding, string? Titel = null, string? Body = null);

    /// <summary>
    /// Publiek toegangspunt op dezelfde voorbereiding als <see cref="VoorbeeldAsync"/> en
    /// <see cref="SubmitAsync"/>: alle gates, dezelfde titel/body-opbouw.
    /// </summary>
    public static async Task<FeedbackVoorbereiding> BereidVoorAsync(
        FeedbackRequest dto, IChatClient chatClient, ILogger log, DateTime? tijdstipUtc = null)
    {
        var voorbereid = await BereidPublicatieVoorAsync(dto, chatClient, log, tijdstipUtc, "Feedback");
        return new FeedbackVoorbereiding(voorbereid.Status, voorbereid.Foutmelding, voorbereid.Titel, voorbereid.Body);
    }

    private sealed record VoorbereidePublicatie(
        FeedbackStatus Status,
        string? Foutmelding,
        string? Titel = null,
        string? Body = null,
        StructuredIssue? Structured = null);

    /// <summary>
    /// De ene plek waar de te publiceren titel + body wordt samengesteld — gedeeld door de
    /// voorbeeldstap en de publicatiestap (#1205), zodat het voorbeeld per constructie dezelfde tekst
    /// oplevert als de publicatie. Een tweede opbouwpad zou het voorbeeld tot een gok maken.
    /// </summary>
    private static async Task<VoorbereidePublicatie> BereidPublicatieVoorAsync(
        FeedbackRequest dto,
        IChatClient chatClient,
        ILogger log,
        DateTime? tijdstipUtc,
        string logLabel)
    {
        if (IsOngeldigType(dto.Type))
        {
            log.LogWarning("{Label} geblokkeerd: onbekend Type-veld (vóór enige verwerking)", logLabel);
            return new VoorbereidePublicatie(FeedbackStatus.OngeldigType, OngeldigTypeMelding());
        }

        if (BevatPii(VerzamelTeCheckenTekst(dto)))
        {
            log.LogWarning("{Label} geblokkeerd: PII gedetecteerd in invoer (vóór AI-aanroep)", logLabel);
            return new VoorbereidePublicatie(FeedbackStatus.PiiGedetecteerd, PiiMelding());
        }

        // Bevestigingsstap: de beheerder heeft deze velden letterlijk in het voorbeeld gezien en
        // bevestigd, dus ze worden hergebruikt in plaats van de AI opnieuw te bevragen. Dat is hier
        // veilig én noodzakelijk:
        // - Noodzakelijk omdat StructureerIssue op temperature 0.2 draait: een tweede aanroep levert
        //   andere tekst op, waardoor het getoonde voorbeeld niet meer zou kloppen met wat er
        //   gepubliceerd wordt — precies de misleiding die #1205 wegneemt.
        // - Veilig omdat dit endpoint achter RequireAdmin zit en dezelfde beheerder via het veld
        //   Beschrijving sowieso al willekeurige tekst in de body kan krijgen; er komt dus geen nieuw
        //   aanvalspad bij.
        // Vertrouwd wordt de client hier desondanks niet. Deze velden waren vóór #1205 altijd
        // AI-output; nu kunnen ze rechtstreeks van de client komen, dus ze krijgen exact dezelfde
        // behandeling als alle andere tekst die de body in gaat: Normaliseer hieronder haalt ze door
        // dezelfde Sanitize (script-escaping) én dezelfde lengte-/aantalgrenzen, en de PII-gate
        // daarna draait onverkort op de uiteindelijke, samengestelde titel + body. Zonder die
        // normalisatie zou een verzoek met een megabyte aan samenvatting integraal in een publiek
        // issue belanden, terwijl Beschrijving wél op 2000 tekens wordt afgekapt.
        var structured = dto.Bevestiging is { } bevestiging
            ? new StructuredIssue(
                string.IsNullOrWhiteSpace(bevestiging.Titel) ? StandaardTitel(dto.Type) : bevestiging.Titel,
                bevestiging.Samenvatting ?? "",
                bevestiging.Acceptatiecriteria is { } criteria ? [.. criteria] : [])
            : await StructureerIssue(chatClient, dto, log);

        // Op beide takken toegepast, niet alleen op de bevestigingstak: zou het voorbeeld een
        // ongenormaliseerde samenvatting teruggeven en de publicatie een genormaliseerde, dan
        // verschilt de gepubliceerde body van wat de beheerder zag — precies de misleiding die
        // #1205 wegneemt.
        structured = Normaliseer(structured);

        var issueBody = FeedbackIssueBody.Bouw(dto, structured, tijdstipUtc ?? DateTime.UtcNow);
        var title = Sanitize(structured.Title, 80);

        // Laatste controle vlak vóór de GitHub-write: op de daadwerkelijke, volledige titel + body —
        // inclusief AI-output (samenvatting, acceptatiecriteria) en alle contextvelden. (#1006)
        if (BevatPii(title) || BevatPii(issueBody))
        {
            log.LogWarning("{Label} geblokkeerd: PII gedetecteerd in uiteindelijke titel/body vóór publicatie naar GitHub", logLabel);
            return new VoorbereidePublicatie(FeedbackStatus.PiiGedetecteerd, PiiMelding());
        }

        return new VoorbereidePublicatie(FeedbackStatus.Ok, null, title, issueBody, structured);
    }

    private static string OngeldigTypeMelding() =>
        $"Ongeldig type. Toegestane waarden: {string.Join(", ", ToegestaneTypes)}.";

    private static string PiiMelding() =>
        "Feedback bevat mogelijk persoonsgegevens. Verwijder e-mailadressen en telefoonnummers en probeer opnieuw.";


    public static Task<(int nummer, string url)> MaakGitHubIssueAsync(
        string pat, string owner, string repo, string title, string body,
        string[] labels, ILogger log) =>
        FeedbackGitHub.MaakGitHubIssueAsync(pat, owner, repo, title, body, labels, log);

    public static Task<(bool gelukt, DateTime? geslotenOpUtc)> HaalIssueSluitingAsync(
        string pat, string owner, string repo, int nummer, ILogger log) =>
        FeedbackGitHub.HaalIssueSluitingAsync(pat, owner, repo, nummer, log);

    public static string[] KiesLabels(string type) => type switch
    {
        "Fout" => ["bug", "type: bug", "via-feedback-widget", "needs-triage"],
        "Verzoek" => ["enhancement", "type: feature", "via-feedback-widget", "needs-triage"],
        _ => ["question", "via-feedback-widget", "needs-triage"]
    };

    public static string VerzamelTeCheckenTekst(FeedbackRequest dto) => FeedbackTekst.VerzamelTeCheckenTekst(dto);

    public static bool BevatPii(string tekst) => FeedbackTekst.BevatPii(tekst);
}
