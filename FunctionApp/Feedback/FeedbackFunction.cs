using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenAI.Chat;

namespace SportlinkFunction.Feedback;

/// <summary>
/// Feedback widget API — issue #129.
///
/// POST /api/feedback/validate
///   Valideert of de gebruikersbeschrijving voldoende informatie bevat.
///   Geeft gerichte aanvulvragen terug als er gaten zijn.
///   Geen rate limiting — validatie is goedkoop en gebruiksvriendelijk.
///
/// POST /api/feedback/submit
///   Structureert de feedback met GPT-4o-mini en maakt een GitHub Issue aan.
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
        var authResult = SportlinkFunction.Admin.EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var dto = JsonConvert.DeserializeObject<FeedbackRequest>(body);
            if (dto == null || string.IsNullOrWhiteSpace(dto.Beschrijving))
                return new BadRequestObjectResult(new { error = "Type en beschrijving zijn verplicht." });

            var apiKey = Environment.GetEnvironmentVariable("OpenAiApiKey")
                ?? throw new InvalidOperationException("OpenAiApiKey niet geconfigureerd");

            var chatClient = new ChatClient("gpt-4o-mini", apiKey);
            var result = await ValideerVolledigheid(chatClient, dto, log);

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij feedback validatie");
            return new ObjectResult(new { error = "Validatie tijdelijk niet beschikbaar." }) { StatusCode = 500 };
        }
    }

    // ── Submit ─────────────────────────────────────────────────────────────────

    [Function("FeedbackSubmit")]
    public static async Task<IActionResult> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback/submit")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("FeedbackSubmit");
        var authResult = SportlinkFunction.Admin.EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;

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
            var repo = Environment.GetEnvironmentVariable("GitHubRepo") ?? "Sportlink-wedstrijdzaken";

            if (string.IsNullOrWhiteSpace(pat) || string.IsNullOrWhiteSpace(owner))
            {
                log.LogWarning("GitHubPat/GitHubOwner niet geconfigureerd — feedback-submit niet mogelijk");
                return new ObjectResult(new { error = "GitHub-integratie niet geconfigureerd. Neem contact op met de beheerder." }) { StatusCode = 503 };
            }

            var apiKey = Environment.GetEnvironmentVariable("OpenAiApiKey")
                ?? throw new InvalidOperationException("OpenAiApiKey niet geconfigureerd");

            var chatClient = new ChatClient("gpt-4o-mini", apiKey);
            var structured = await StructureerIssue(chatClient, dto, log);

            var issueBody = BouwIssueBody(dto, structured);
            var labels = KiesLabels(dto.Type);
            var title = Sanitize(structured.Title, 80);

            var (issueNummer, issueUrl) = await MaakGitHubIssueAsync(pat, owner, repo, title, issueBody, labels, log);

            return new OkObjectResult(new { issueNummer, issueUrl });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij feedback submit");
            return new ObjectResult(new { error = "Indienen mislukt. Probeer het opnieuw." }) { StatusCode = 500 };
        }
    }

    // ── AI: volledigheid valideren ─────────────────────────────────────────────

    private static async Task<ValidateResponse> ValideerVolledigheid(
        ChatClient chatClient, FeedbackRequest dto, ILogger log)
    {
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
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        var completion = await chatClient.CompleteChatAsync(messages, options);
        var json = completion.Value.Content[0].Text;
        log.LogDebug("Validate AI response: {Json}", json);

        var parsed = JObject.Parse(json);
        var volledig = parsed["volledig"]?.Value<bool>() ?? false;
        var vragen = parsed["vragen"]?.ToObject<List<string>>() ?? [];

        return new ValidateResponse(volledig, vragen);
    }

    // ── AI: issue structureren ─────────────────────────────────────────────────

    private static async Task<StructuredIssue> StructureerIssue(
        ChatClient chatClient, FeedbackRequest dto, ILogger log)
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
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.2f,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        var completion = await chatClient.CompleteChatAsync(messages, options);
        var json = completion.Value.Content[0].Text;
        log.LogDebug("Submit AI response: {Json}", json);

        var parsed = JObject.Parse(json);
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

    private static string Sanitize(string? input, int maxLen)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var clean = input
            .Replace("<script", "&lt;script", StringComparison.OrdinalIgnoreCase)
            .Replace("</script>", "&lt;/script&gt;", StringComparison.OrdinalIgnoreCase);
        return clean.Length > maxLen ? clean[..maxLen] + "…" : clean;
    }

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
