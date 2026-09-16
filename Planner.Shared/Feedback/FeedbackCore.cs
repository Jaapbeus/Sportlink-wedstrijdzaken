using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;

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

/// <summary>Status van een validate/submit-uitkomst — bepaalt welke HTTP-respons de tier-entrypoint bouwt.</summary>
public enum FeedbackStatus
{
    /// <summary>Verwerkt zonder gate-blokkade.</summary>
    Ok,

    /// <summary>Type valt buiten de vaste keuzes (#1127) — geblokkeerd vóór enige verwerking (HTTP 400).</summary>
    OngeldigType,

    /// <summary>PII gedetecteerd in invoer of AI-output (#1006) — geblokkeerd vóór AI- resp. GitHub-aanroep (HTTP 422).</summary>
    PiiGedetecteerd
}

public sealed record FeedbackValidatieResultaat(
    FeedbackStatus Status,
    string? Foutmelding,
    bool Volledig = false,
    IReadOnlyList<string>? Vragen = null);

public sealed record FeedbackSubmitResultaat(
    FeedbackStatus Status,
    string? Foutmelding,
    int IssueNummer = 0,
    string? IssueUrl = null);

/// <summary>
/// Uitkomst van de voorbeeldstap (#1205): de exacte titel en body die bij publicatie naar GitHub
/// zouden gaan, plus de losse AI-velden die de bevestigingsstap onveranderd terugstuurt. Bij een
/// geblokkeerde status blijven alle tekstvelden leeg — een geweigerd voorbeeld geeft nooit de
/// samengestelde tekst terug.
/// </summary>
public sealed record FeedbackVoorbeeldResultaat(
    FeedbackStatus Status,
    string? Foutmelding,
    string? Titel = null,
    string? Body = null,
    string? Samenvatting = null,
    IReadOnlyList<string>? Acceptatiecriteria = null);

/// <summary>
/// De door de AI geproduceerde velden zoals de beheerder ze in het voorbeeld heeft gezien (#1205).
/// Staat dit gevuld op een submit, dan publiceert de server exact die tekst in plaats van de AI
/// opnieuw aan te roepen.
/// </summary>
public sealed class FeedbackBevestiging
{
    // Nullable: deze waarden komen rechtstreeks uit de gedeserialiseerde requestbody, dus een
    // ontbrekend of null veld is een realistische invoer en geen programmeerfout.
    public string? Titel { get; set; }
    public string? Samenvatting { get; set; }
    public List<string>? Acceptatiecriteria { get; set; }
}

public sealed class FeedbackRequest
{
    public string Type { get; set; } = "";
    public string Beschrijving { get; set; } = "";
    public List<VraagAntwoord>? VragenAntwoorden { get; set; }
    public FeedbackContext? Context { get; set; }

    /// <summary>
    /// Alleen gevuld op de bevestigingsstap na een voorbeeld (#1205). Zie
    /// <see cref="FeedbackCore.SubmitAsync"/> voor waarom deze clientwaarden hier veilig zijn.
    /// </summary>
    public FeedbackBevestiging? Bevestiging { get; set; }
}

public sealed class VraagAntwoord
{
    public string Vraag { get; set; } = "";
    public string Antwoord { get; set; } = "";
}

public sealed class FeedbackContext
{
    public string Pagina { get; set; } = "";
    public string Versie { get; set; } = "";
    public string Rol { get; set; } = "";
    public string Browser { get; set; } = "";
}

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

        var issueBody = BouwIssueBody(dto, structured, tijdstipUtc ?? DateTime.UtcNow);
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

    // ── AI: gedeeld JSON-ophaal-en-parse-blok ──────────────────────────────────

    private static async Task<JObject> RoepAiJsonAanAsync(
        IChatClient chatClient, List<ChatMessage> messages, float temperature, string logLabel, ILogger log)
    {
        var options = new ChatOptions
        {
            Temperature = temperature,
            ResponseFormat = ChatResponseFormat.Json
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await chatClient.GetResponseAsync(messages, options);
        sw.Stop();
        var json = response.Text ?? "";
        // Nooit de ruwe AI-respons loggen (#1006) — die kan ongecontroleerde, mogelijk persoonsgegevens
        // bevattende tekst bevatten. Alleen veilige technische metadata.
        log.LogDebug("{Label} AI response ontvangen: {Lengte} tekens in {DuurMs} ms", logLabel, json.Length, sw.ElapsedMilliseconds);

        return JObject.Parse(json);
    }

    // ── AI: volledigheid valideren ─────────────────────────────────────────────

    private static async Task<ValidateResponse> ValideerVolledigheid(
        IChatClient chatClient, FeedbackRequest dto, ILogger log)
    {
        // Als de gebruiker al antwoorden heeft gegeven op aanvulvragen, accepteer direct.
        // Re-validatie leidt tot dezelfde vragen omdat het AI-model eerder gestelde vragen
        // opnieuw stelt ondanks het antwoord — de antwoorden vullen de gaten per definitie.
        if (dto.VragenAntwoorden?.Any(qa => !string.IsNullOrWhiteSpace(qa.Antwoord)) == true)
            return new ValidateResponse(true, []);

        var beschrijving = Sanitize(dto.Beschrijving, 2000);
        var paginaInfo = string.IsNullOrWhiteSpace(dto.Context?.Pagina) ? "" : $"Pagina: {dto.Context.Pagina}\n";
        var qaBlok = BouwQaBlok(dto.VragenAntwoorden);

        var systemPrompt = """
            Je beoordeelt of feedback van een clubbeheerder voldoende informatie bevat om te worden opgelost.

            Regels per type:
            - 'Fout': minimaal vereist — wat gaat er mis, en wat werd verwacht.
            - 'Verzoek': minimaal vereist — wat wil men bereiken.
            - 'Vraag': bijna altijd voldoende tenzij compleet onduidelijk.

            Geef uitsluitend JSON in dit formaat:
            { "volledig": true/false, "vragen": ["..."] }

            Als volledig: lege vragen-array.
            Als niet volledig: max 3 korte, vriendelijke aanvulvragen in begrijpelijk Nederlands.
            Nooit technisch jargon. Nooit vragen naar dingen die al beantwoord zijn.
            """;

        var userPrompt = $"""
            Type: {dto.Type}
            {paginaInfo}Beschrijving: "{beschrijving}"
            {qaBlok}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var parsed = await RoepAiJsonAanAsync(chatClient, messages, 0.1f, "Validate", log);
        var volledig = parsed["volledig"]?.Value<bool>() ?? false;
        var vragen = parsed["vragen"]?.ToObject<List<string>>() ?? [];

        return new ValidateResponse(volledig, vragen);
    }

    // ── AI: issue structureren ─────────────────────────────────────────────────

    private static async Task<StructuredIssue> StructureerIssue(
        IChatClient chatClient, FeedbackRequest dto, ILogger log)
    {
        var beschrijving = Sanitize(dto.Beschrijving, 2000);
        var qaBlok = BouwQaBlok(dto.VragenAntwoorden);

        var systemPrompt = """
            Je vertaalt gebruikersfeedback van een clubbeheerder naar een gestructureerd GitHub issue voor een developer.

            Geef uitsluitend JSON in dit formaat:
            {
              "title": "korte issue titel, max 70 tekens",
              "samenvatting": "1-2 zinnen die het probleem of verzoek beschrijven voor de developer",
              "acceptatiecriteria": ["concreet testbaar criterium", "criterium 2"]
            }

            Voor een bug:
            - Titel: beschrijft wat er mis gaat (niet 'gebruiker meldt...')
            - Samenvatting: wat de gebruiker deed, wat er fout ging, wat verwacht werd
            - Criteria: testbare verbeteringen (elk < 80 tekens, max 5 stuks)

            Voor een verzoek:
            - Titel: "Voeg X toe" of "Maak X mogelijk"
            - Samenvatting: gewenste gedrag en reden
            - Criteria: implementatiestappen als checkbox

            Schrijf technisch, voor een developer, niet voor de gebruiker.
            """;

        var userPrompt = $"""
            Type: {dto.Type}
            Pagina: {dto.Context?.Pagina ?? "onbekend"}
            Versie: {dto.Context?.Versie ?? "?"}

            Beschrijving gebruiker: "{beschrijving}"
            {qaBlok}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var parsed = await RoepAiJsonAanAsync(chatClient, messages, 0.2f, "Submit", log);
        return new StructuredIssue(
            parsed["title"]?.Value<string>() ?? StandaardTitel(dto.Type),
            parsed["samenvatting"]?.Value<string>() ?? "",
            parsed["acceptatiecriteria"]?.ToObject<List<string>>() ?? []
        );
    }

    private static string StandaardTitel(string type) => $"[{type}] Gebruikersmelding";

    // Grenzen voor de gestructureerde velden. De samenvatting is volgens de system prompt 1-2 zinnen;
    // 500 tekens sluit aan bij de bestaande grens voor een antwoord op een aanvulvraag en laat een
    // uitgebreide samenvatting ruim toe. Acceptatiecriteria vraagt de prompt om "max 5 stuks" — dat
    // is hier de harde grens, zodat een client die er 500 stuurt wordt afgekapt in plaats van ze
    // allemaal in een publiek issue te krijgen.
    private const int MaxSamenvattingLengte = 500;
    private const int MaxAcceptatiecriteria = 5;
    private const int MaxAcceptatiecriteriumLengte = 120;

    /// <summary>
    /// Brengt de gestructureerde velden binnen dezelfde sanitizer en grenzen als alle andere tekst die
    /// de issuebody in gaat (#1205). Wordt op zowel AI-output als bevestigde clientwaarden toegepast,
    /// zodat het voorbeeld en de publicatie per constructie dezelfde tekst opleveren.
    /// <see cref="Sanitize"/> is idempotent, dus dubbel toepassen (hier én in
    /// <see cref="BouwIssueBody"/>) verandert het resultaat niet.
    /// </summary>
    private static StructuredIssue Normaliseer(StructuredIssue structured) => new(
        structured.Title,
        Sanitize(structured.Samenvatting, MaxSamenvattingLengte),
        [.. structured.Acceptatiecriteria
            .Take(MaxAcceptatiecriteria)
            .Select(c => Sanitize(c, MaxAcceptatiecriteriumLengte))]);

    // ── GitHub Issue aanmaken ──────────────────────────────────────────────────

    /// <summary>
    /// Standaardimplementatie van de GitHub-issue-aanroep — geen tier-afhankelijkheid (enkel
    /// pat/owner/repo als parameters). Tier-entrypoints geven dit door als de
    /// <c>maakGitHubIssueAsync</c>-delegate aan <see cref="SubmitAsync"/>; tests injecteren daar een
    /// fake in plaats van deze methode.
    /// </summary>
    public static async Task<(int nummer, string url)> MaakGitHubIssueAsync(
        string pat, string owner, string repo, string title, string body,
        string[] labels, ILogger log)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SportlinkFeedbackWidget/2.0");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var payload = JsonConvert.SerializeObject(new { title, body, labels });
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues";
        var resp = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            // Retry zonder custom labels bij 422 (labels bestaan niet)
            if ((int)resp.StatusCode == 422)
            {
                log.LogWarning("GitHub 422 bij labels {Labels} — retry zonder custom labels", string.Join(",", labels));
                var fallbackLabels = labels.Where(l => l == "bug" || l == "enhancement" || l == "question").ToArray();
                var fallbackPayload = JsonConvert.SerializeObject(new { title, body, labels = fallbackLabels });
                resp = await http.PostAsync(url, new StringContent(fallbackPayload, Encoding.UTF8, "application/json"));
            }

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                log.LogWarning("GitHub issue aanmaken mislukt: HTTP {Status} — {Err}", (int)resp.StatusCode, err);
                throw new InvalidOperationException($"GitHub API HTTP {(int)resp.StatusCode}");
            }
        }

        var json = await resp.Content.ReadAsStringAsync();
        dynamic created = JsonConvert.DeserializeObject<dynamic>(json)!;
        int nummer = (int)created.number;
        string issueUrl = (string)created.html_url;
        log.LogInformation("GitHub issue #{Nr} aangemaakt via feedback widget", nummer);
        return (nummer, issueUrl);
    }

    // ── Issue body samenstelllen ───────────────────────────────────────────────

    private static string BouwIssueBody(FeedbackRequest dto, StructuredIssue structured, DateTime tijdstipUtc)
    {
        var typeIcon = dto.Type switch { "Fout" => "🐛", "Verzoek" => "💡", _ => "❓" };
        var ctx = dto.Context;
        var beschrijving = Sanitize(dto.Beschrijving, 2000);
        var tijdstip = tijdstipUtc.ToString("yyyy-MM-dd HH:mm") + " UTC";

        var sb = new StringBuilder();
        sb.AppendLine("## 🗣️ Gemeld via feedback widget");
        sb.AppendLine();
        sb.AppendLine("| Veld | Waarde |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Type | {typeIcon} {dto.Type} |");
        if (ctx != null)
        {
            sb.AppendLine($"| Pagina | `{ctx.Pagina}` |");
            sb.AppendLine($"| Versie | {ctx.Versie} |");
            sb.AppendLine($"| Omgeving | {(ctx.Versie.Contains("dev", StringComparison.OrdinalIgnoreCase) ? "ontwikkeling" : "productie")} |");
            if (!string.IsNullOrWhiteSpace(ctx.Browser))
                sb.AppendLine($"| Browser | {ctx.Browser[..Math.Min(ctx.Browser.Length, 80)]} |");
        }
        sb.AppendLine($"| Tijdstip | {tijdstip} |");
        sb.AppendLine();

        sb.AppendLine("## Beschrijving (eigen woorden gebruiker)");
        sb.AppendLine();
        sb.AppendLine($"> {beschrijving.Replace("\n", "\n> ")}");
        sb.AppendLine();

        if (dto.VragenAntwoorden?.Count > 0)
        {
            sb.AppendLine("## Aanvullende context");
            sb.AppendLine();
            foreach (var qa in dto.VragenAntwoorden)
            {
                var vraag = Sanitize(qa.Vraag, 200);
                var antwoord = Sanitize(qa.Antwoord, 500);
                sb.AppendLine($"**{vraag}:** {antwoord}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Analyse");
        sb.AppendLine();
        sb.AppendLine(structured.Samenvatting);
        sb.AppendLine();

        if (structured.Acceptatiecriteria.Count > 0)
        {
            sb.AppendLine("## Acceptatiecriteria");
            sb.AppendLine();
            foreach (var criterium in structured.Acceptatiecriteria)
                sb.AppendLine($"- [ ] {Sanitize(criterium, MaxAcceptatiecriteriumLengte)}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine($"*Aangemaakt via BlazorAdmin feedback widget v{ctx?.Versie ?? "?"}*");

        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    public static string[] KiesLabels(string type) => type switch
    {
        "Fout" => ["bug", "type: bug", "via-feedback-widget", "needs-triage"],
        "Verzoek" => ["enhancement", "type: feature", "via-feedback-widget", "needs-triage"],
        _ => ["question", "via-feedback-widget", "needs-triage"]
    };

    private static string BouwQaBlok(List<VraagAntwoord>? qaList)
    {
        if (qaList == null || qaList.Count == 0) return "";
        var sb = new StringBuilder("\nAanvullende context:\n");
        foreach (var qa in qaList)
            sb.AppendLine($"- {Sanitize(qa.Vraag, 200)}: {Sanitize(qa.Antwoord, 500)}");
        return sb.ToString();
    }

    /// <summary>
    /// Verzamelt alle velden van een <see cref="FeedbackRequest"/> die ooit in een AI-prompt of in de
    /// gepubliceerde GitHub-body terechtkomen, zodat de PII-gate de volledige invoer controleert in
    /// plaats van alleen Beschrijving + Antwoord (#1006 — de oorspronkelijke #427-gate miste
    /// Context.Pagina/Versie/Browser en elke Vraag). Type is sinds #1127 ook opgenomen: de
    /// <see cref="IsOngeldigType"/>-check hierboven maakt PII in Type al structureel onmogelijk, maar
    /// deze verzameling blijft Type meenemen als extra, onafhankelijke laag — mocht die allowlist ooit
    /// verdwijnen, dan blokkeert deze gate nog steeds.
    /// </summary>
    public static string VerzamelTeCheckenTekst(FeedbackRequest dto)
    {
        var delen = new List<string?> { dto.Type, dto.Beschrijving, dto.Context?.Pagina, dto.Context?.Versie, dto.Context?.Browser };
        if (dto.VragenAntwoorden != null)
        {
            foreach (var qa in dto.VragenAntwoorden)
            {
                delen.Add(qa.Vraag);
                delen.Add(qa.Antwoord);
            }
        }
        return string.Join(" ", delen.Where(d => !string.IsNullOrWhiteSpace(d)));
    }

    // PII-gate: detecteert e-mailadressen en Nederlandse telefoonnummers. (#427, uitgebreid #1006)
    // Blokkeert publicatie naar GitHub als mogelijke persoonsgegevens aanwezig zijn.
    // Let op: dit is regex-detectie van e-mail/telefoon — geen algemene garantie tegen elke vorm van
    // persoonsgegevens of secrets (bijv. namen, adressen, BSN's worden niet herkend).
    public static bool BevatPii(string tekst)
    {
        if (string.IsNullOrWhiteSpace(tekst)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(tekst,
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}"))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(tekst,
            @"(\+31|0031|06)[\s\-]?\d{2}[\s\-]?\d{6,8}|0\d{1,2}[\s\-]\d{6,8}"))
            return true;
        return false;
    }

    private static string Sanitize(string? input, int maxLen)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var clean = input
            .Replace("<script", "&lt;script", StringComparison.OrdinalIgnoreCase)
            .Replace("</script>", "&lt;/script&gt;", StringComparison.OrdinalIgnoreCase);
        return clean.Length > maxLen ? clean[..maxLen] + "…" : clean;
    }

    private sealed record ValidateResponse(bool Volledig, List<string> Vragen);
    private sealed record StructuredIssue(string Title, string Samenvatting, List<string> Acceptatiecriteria);
}

/// <summary>
/// Rate limiter voor feedback-submits — gedeeld tussen beide tiers omdat elke club-deployment maar
/// één tier tegelijk draait (#1130).
///
/// #610 — bewuste keuze: de teller is in-memory en geldt dus per Consumption-plan-instance, niet
/// globaal. Bij opschaling kan de effectieve limiet een veelvoud van <see cref="MaxSubmissiesPerVenster"/>
/// zijn. Acceptabel omdat dit endpoint admin-only is (<c>RequireAdmin</c>) en de limiet bedoeld is
/// als rem tegen per ongeluk doorklikken, niet als beveiligingsgrens tegen een aanvaller. Een
/// gedeelde store (SQL/Table Storage) zou een extra round-trip en onderhoud kosten zonder dat het
/// risico dat rechtvaardigt. Wordt dit ooit een publiek endpoint, dan is een gedeelde teller wél nodig.
/// </summary>
public static class FeedbackRateLimiter
{
    public const int MaxSubmissiesPerVenster = 5;
    private static readonly TimeSpan RateLimitVenster = TimeSpan.FromMinutes(10);
    private static readonly Queue<DateTime> _submits = new();
    private static readonly object _rateLock = new();

    public static bool TryAcquireSubmitSlot()
    {
        lock (_rateLock)
        {
            var cutoff = DateTime.UtcNow - RateLimitVenster;
            while (_submits.TryPeek(out var first) && first < cutoff)
                _submits.Dequeue();
            if (_submits.Count >= MaxSubmissiesPerVenster) return false;
            _submits.Enqueue(DateTime.UtcNow);
            return true;
        }
    }
}
