using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace FunctionApp.Postgres.Feedback;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Feedback/FeedbackFunction.cs</c> (#966) — vrijwel
/// woordelijke kopie. Geen SQL-afhankelijkheid in het origineel: dit bestand raakt uitsluitend
/// <see cref="IChatClient"/> (provider-agnostisch, hier geregistreerd in Program.cs) en de GitHub
/// API. Precies dat maakte dit bestand onzichtbaar voor #860's inventarisatie (die telt op
/// <c>SqlConnection</c>) — zie de audit op #889/#966.
///
/// POST /api/feedback/validate
///   Valideert of de gebruikersbeschrijving voldoende informatie bevat.
///   Geen rate limiting — validatie is goedkoop en gebruiksvriendelijk.
///
/// POST /api/feedback/submit
///   Structureert de feedback met AI en maakt een GitHub Issue aan.
///   Rate limiting: max 5 per 10 minuten (globaal).
/// </summary>
public static class FeedbackFunction
{
    private const int MaxSubmissiesPerVenster = 5;
    private static readonly TimeSpan RateLimitVenster = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentQueue<DateTime> _submits = new();
    private static readonly object _rateLock = new();

    // ── Validate ──────────────────────────────────────────────────────────────

    [Function("FeedbackValidate")]
    public static async Task<IActionResult> Validate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback/validate")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("FeedbackValidate");
        var correlationId = Admin.EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = Admin.EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<FeedbackRequest>(body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.Beschrijving))
                return new BadRequestObjectResult(new { error = "Type en beschrijving zijn verplicht." });

            var chatClient = context.InstanceServices.GetService<IChatClient>()
                ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");
            return await ValidateCoreAsync(dto, chatClient, log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij feedback validatie");
            return new ObjectResult(new { error = "Validatie tijdelijk niet beschikbaar." }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Testbare kern van <see cref="Validate"/>, los van <see cref="HttpRequest"/>/<see cref="FunctionContext"/>
    /// zodat regressietests een <see cref="IChatClient"/>-fake kunnen injecteren (#1006). PII-gate draait
    /// vóór de AI-aanroep en dekt alle velden die de prompt in kan gaan — niet alleen Beschrijving/Antwoord.
    /// </summary>
    internal static async Task<IActionResult> ValidateCoreAsync(FeedbackRequest dto, IChatClient chatClient, ILogger log)
    {
        if (BevatPii(VerzamelTeCheckenTekst(dto)))
        {
            log.LogWarning("Feedback-validatie geblokkeerd: PII gedetecteerd in invoer (vóór AI-aanroep)");
            return new ObjectResult(new {
                error = "Feedback bevat mogelijk persoonsgegevens. Verwijder e-mailadressen en telefoonnummers en probeer opnieuw."
            }) { StatusCode = 422 };
        }

        var result = await ValideerVolledigheid(chatClient, dto, log);
        return new OkObjectResult(result);
    }

    // ── Submit ─────────────────────────────────────────────────────────────────

    [Function("FeedbackSubmit")]
    public static async Task<IActionResult> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback/submit")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("FeedbackSubmit");
        var correlationId = Admin.EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = Admin.EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

        if (!TryAcquireSubmitSlot())
            return new ObjectResult(new { error = $"Limiet bereikt: maximaal {MaxSubmissiesPerVenster} meldingen per 10 minuten." }) { StatusCode = 429 };

        try
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<FeedbackRequest>(body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.Beschrijving))
                return new BadRequestObjectResult(new { error = "Beschrijving is verplicht." });

            var pat = Environment.GetEnvironmentVariable("GitHubPat");
            var owner = Environment.GetEnvironmentVariable("GitHubOwner")
                     ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER") ?? "";
            // GitHubRepo is net als GitHubOwner verplicht: een stille fallback op de upstream-repo-naam
            // geeft een fork met een andere naam een verwarrende 404 i.p.v. een configuratiefout. (#607)
            var repo = Environment.GetEnvironmentVariable("GitHubRepo");

            if (string.IsNullOrWhiteSpace(pat) || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                log.LogWarning("GitHubPat/GitHubOwner/GitHubRepo niet volledig geconfigureerd — feedback-submit niet mogelijk");
                return new ObjectResult(new { error = "GitHub-integratie niet geconfigureerd. Neem contact op met de beheerder." }) { StatusCode = 503 };
            }

            var chatClient = context.InstanceServices.GetService<IChatClient>()
                ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");

            Task<(int nummer, string url)> MaakIssue(string title, string body, string[] labels) =>
                MaakGitHubIssueAsync(pat, owner, repo, title, body, labels, log);

            return await SubmitCoreAsync(dto, chatClient, MaakIssue, log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij feedback submit");
            return new ObjectResult(new { error = "Indienen mislukt. Probeer het opnieuw." }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Testbare kern van <see cref="Submit"/>, los van <see cref="HttpRequest"/>/<see cref="FunctionContext"/>
    /// en de echte GitHub-<see cref="HttpClient"/> zodat regressietests een <see cref="IChatClient"/>-fake en
    /// een GitHub-fake kunnen injecteren (#1006).
    ///
    /// Twee PII-gates, niet één:
    /// 1. Vóór de AI-aanroep — over alle velden die de prompt in kunnen gaan (Context.Pagina/Versie/Browser,
    ///    elke Vraag én Antwoord), niet alleen Beschrijving/Antwoord zoals de oorspronkelijke #427-gate.
    /// 2. Vlak vóór de GitHub-write — over de daadwerkelijke, uiteindelijke titel + body, dus inclusief
    ///    AI-gegenereerde Samenvatting/acceptatiecriteria. AI-output wordt nooit impliciet vertrouwd als
    ///    publiceerbare tekst.
    /// Een blocked input doet daarom nooit een AI-aanroep; een blocked output doet nooit een GitHub-aanroep.
    /// </summary>
    internal static async Task<IActionResult> SubmitCoreAsync(
        FeedbackRequest dto,
        IChatClient chatClient,
        Func<string, string, string[], Task<(int nummer, string url)>> maakGitHubIssueAsync,
        ILogger log)
    {
        if (BevatPii(VerzamelTeCheckenTekst(dto)))
        {
            log.LogWarning("Feedback geblokkeerd: PII gedetecteerd in invoer (vóór AI-aanroep)");
            return new ObjectResult(new {
                error = "Feedback bevat mogelijk persoonsgegevens. Verwijder e-mailadressen en telefoonnummers en probeer opnieuw."
            }) { StatusCode = 422 };
        }

        var structured = await StructureerIssue(chatClient, dto, log);

        var issueBody = BouwIssueBody(dto, structured);
        var labels = KiesLabels(dto.Type);
        var title = Sanitize(structured.Title, 80);

        // Laatste controle vlak vóór de GitHub-write: op de daadwerkelijke, volledige titel + body —
        // inclusief AI-output (samenvatting, acceptatiecriteria) en alle contextvelden. (#1006)
        if (BevatPii(title) || BevatPii(issueBody))
        {
            log.LogWarning("Feedback geblokkeerd: PII gedetecteerd in uiteindelijke titel/body vóór publicatie naar GitHub");
            return new ObjectResult(new {
                error = "Feedback bevat mogelijk persoonsgegevens. Verwijder e-mailadressen en telefoonnummers en probeer opnieuw."
            }) { StatusCode = 422 };
        }

        var (issueNummer, issueUrl) = await maakGitHubIssueAsync(title, issueBody, labels);

        return new OkObjectResult(new { issueNummer, issueUrl });
    }

    // ── AI: gedeeld JSON-ophaal-en-parse-blok ──────────────────────────────────

    private static async Task<JObject> RoepAiJsonAanAsync(
        IChatClient chatClient, List<Microsoft.Extensions.AI.ChatMessage> messages, float temperature, string logLabel, ILogger log)
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

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
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

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var parsed = await RoepAiJsonAanAsync(chatClient, messages, 0.2f, "Submit", log);
        return new StructuredIssue(
            parsed["title"]?.Value<string>() ?? $"[{dto.Type}] Gebruikersmelding",
            parsed["samenvatting"]?.Value<string>() ?? "",
            parsed["acceptatiecriteria"]?.ToObject<List<string>>() ?? []
        );
    }

    // ── GitHub Issue aanmaken ──────────────────────────────────────────────────

    private static async Task<(int nummer, string url)> MaakGitHubIssueAsync(
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

    private static string BouwIssueBody(FeedbackRequest dto, StructuredIssue structured)
    {
        var typeIcon = dto.Type switch { "Fout" => "🐛", "Verzoek" => "💡", _ => "❓" };
        var ctx = dto.Context;
        var beschrijving = Sanitize(dto.Beschrijving, 2000);
        var tijdstip = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC";

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
                sb.AppendLine($"- [ ] {Sanitize(criterium, 120)}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine($"*Aangemaakt via BlazorAdmin feedback widget v{ctx?.Versie ?? "?"}*");

        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string[] KiesLabels(string type) => type switch
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
    /// Context.Pagina/Versie/Browser en elke Vraag).
    /// </summary>
    private static string VerzamelTeCheckenTekst(FeedbackRequest dto)
    {
        var delen = new List<string?> { dto.Beschrijving, dto.Context?.Pagina, dto.Context?.Versie, dto.Context?.Browser };
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
    private static bool BevatPii(string tekst)
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

    /// <summary>
    /// Rate limiter voor feedback-submits.
    ///
    /// #610 — bewuste keuze: de teller is in-memory en geldt dus per Consumption-plan-instance, niet
    /// globaal. Bij opschaling kan de effectieve limiet een veelvoud van
    /// <see cref="MaxSubmissiesPerVenster"/> zijn. Acceptabel omdat dit endpoint admin-only is
    /// (<c>RequireAdmin</c>) en de limiet bedoeld is als rem tegen per ongeluk doorklikken, niet als
    /// beveiligingsgrens tegen een aanvaller. Een gedeelde store (SQL/Table Storage) zou een extra
    /// round-trip en onderhoud kosten zonder dat het risico dat rechtvaardigt. Wordt dit ooit een
    /// publiek endpoint, dan is een gedeelde teller wél nodig.
    /// </summary>
    private static bool TryAcquireSubmitSlot()
    {
        lock (_rateLock)
        {
            var cutoff = DateTime.UtcNow - RateLimitVenster;
            while (_submits.TryPeek(out var first) && first < cutoff)
                _submits.TryDequeue(out _);
            if (_submits.Count >= MaxSubmissiesPerVenster) return false;
            _submits.Enqueue(DateTime.UtcNow);
            return true;
        }
    }

    // ── Request / Response modellen ────────────────────────────────────────────

    internal sealed class FeedbackRequest
    {
        public string Type { get; set; } = "";
        public string Beschrijving { get; set; } = "";
        public List<VraagAntwoord>? VragenAntwoorden { get; set; }
        public FeedbackContext? Context { get; set; }
    }

    internal sealed class VraagAntwoord
    {
        public string Vraag { get; set; } = "";
        public string Antwoord { get; set; } = "";
    }

    internal sealed class FeedbackContext
    {
        public string Pagina { get; set; } = "";
        public string Versie { get; set; } = "";
        public string Rol { get; set; } = "";
        public string Browser { get; set; } = "";
    }

    private sealed record ValidateResponse(bool Volledig, List<string> Vragen);
    private sealed record StructuredIssue(string Title, string Samenvatting, List<string> Acceptatiecriteria);
}
