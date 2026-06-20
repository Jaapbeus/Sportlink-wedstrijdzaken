using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SportlinkFunction.Infrastructure;

/// <summary>
/// Rapporteert onverwachte exceptions als GitHub Issues.
/// Deduplicatie op fingerprint (zie SystemUtilities.ComputeFingerprint):
///   - Bestaand open issue → voeg comment toe
///   - Geen open issue → maak nieuw issue aan
///   - Al gerapporteerd binnen 24u → overslaan (rate-limiting, issue #106)
///
/// Vereiste environment variables:
///   GitHubPat   — fine-grained PAT met issues:write scope (zie #103)
///   GitHubOwner — GitHub organisatie of gebruikersnaam (default: env GITHUB_REPOSITORY_OWNER)
///   GitHubRepo  — repository naam (default: "Sportlink-wedstrijdzaken")
///
/// Wanneer GitHubPat niet geconfigureerd is, wordt alles stil overgeslagen.
/// </summary>
public static class GitHubIssueReporter
{
    private static readonly Dictionary<string, DateTime> _recentlyReported = new();
    private static readonly object _lock = new();

    private const int MaxStackTraceLines = 50;
    private const int RateLimitHours = 24;

    public static async Task ReportAsync(Exception ex, string functionName, ILogger log)
    {
        var pat = Environment.GetEnvironmentVariable("GitHubPat");
        if (string.IsNullOrWhiteSpace(pat))
        {
            log.LogDebug("GitHubPat niet geconfigureerd — exception-reporting naar GitHub overgeslagen");
            return;
        }

        var owner = Environment.GetEnvironmentVariable("GitHubOwner")
                 ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER")
                 ?? "";
        var repo = Environment.GetEnvironmentVariable("GitHubRepo");
        if (string.IsNullOrWhiteSpace(repo))
        {
            log.LogError("GitHubRepo niet geconfigureerd — issue-reporting overgeslagen (misconfiguratie)");
            return;
        }

        if (string.IsNullOrWhiteSpace(owner))
        {
            log.LogWarning("GitHubOwner niet geconfigureerd — issue-reporting overgeslagen");
            return;
        }

        var fp = SystemUtilities.ComputeFingerprint(ex);

        lock (_lock)
        {
            if (_recentlyReported.TryGetValue(fp, out var last)
                && (DateTime.UtcNow - last).TotalHours < RateLimitHours)
            {
                log.LogInformation("Exception fp:{Fp} al gerapporteerd binnen {H}u — overgeslagen", fp, RateLimitHours);
                return;
            }
            _recentlyReported[fp] = DateTime.UtcNow;
        }

        try
        {
            using var http = BuildHttpClient(pat);
            var existing = await SearchIssueAsync(http, owner, repo, fp, log);

            if (existing.HasValue)
            {
                if (existing.Value.isClosed)
                    await ReopenIssueAsync(http, owner, repo, existing.Value.number, log);
                await AddCommentAsync(http, owner, repo, existing.Value.number, ex, functionName, log);
            }
            else
                await CreateIssueAsync(http, owner, repo, fp, ex, functionName, log);
        }
        catch (Exception reportEx)
        {
            log.LogWarning(reportEx, "GitHubIssueReporter: fout bij rapporteren van exception fp:{Fp}", fp);
        }
    }

    private static HttpClient BuildHttpClient(string pat)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SportlinkFunction/2.0");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    private static async Task<(int number, bool isClosed)?> SearchIssueAsync(
        HttpClient http, string owner, string repo, string fp, ILogger log)
    {
        // Zoek in open én gesloten issues — zo wordt nooit een duplicaat aangemaakt
        // ook niet na een cold start of nadat een issue eerder gesloten werd.
        var query = Uri.EscapeDataString($"[fp:{fp}] in:title repo:{owner}/{repo}");
        var url = $"https://api.github.com/search/issues?q={query}&per_page=1&sort=created&order=desc";
        var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("GitHub search API: HTTP {Status}", (int)resp.StatusCode);
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync();
        dynamic result = JsonConvert.DeserializeObject<dynamic>(json)!;
        int count = (int)result.total_count;
        if (count > 0)
        {
            int number = (int)result.items[0].number;
            string state = (string)result.items[0].state;
            bool isClosed = state == "closed";
            log.LogInformation("Bestaand GitHub issue #{Nr} ({State}) gevonden voor fp:{Fp}", number, state, fp);
            return (number, isClosed);
        }
        return null;
    }

    private static async Task ReopenIssueAsync(
        HttpClient http, string owner, string repo, int issueNumber, ILogger log)
    {
        var payload = JsonConvert.SerializeObject(new { state = "open" });
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}";
        var resp = await http.PatchAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
        if (resp.IsSuccessStatusCode)
            log.LogInformation("GitHub issue #{Nr} heropend (opnieuw opgetreden)", issueNumber);
        else
            log.LogWarning("GitHub issue heropenen mislukt: HTTP {Status}", (int)resp.StatusCode);
    }

    private static async Task AddCommentAsync(
        HttpClient http, string owner, string repo, int issueNumber,
        Exception ex, string functionName, ILogger log)
    {
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);

        var body = $"🔁 Opnieuw opgetreden op {nlTijd:dd-MM-yyyy HH:mm} in functie `{functionName}`\n\n"
                 + $"**Exception:** `{ex.GetType().FullName}: {SanitizeForPublic(ex.Message)}`\n\n"
                 + $"**Stacktrace:**\n```\n{TruncateStackTrace(SanitizeForPublic(ex.StackTrace))}\n```";

        var payload = JsonConvert.SerializeObject(new { body });
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}/comments";
        var resp = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));

        if (resp.IsSuccessStatusCode)
            log.LogInformation("Comment toegevoegd aan GitHub issue #{Nr}", issueNumber);
        else
            log.LogWarning("GitHub comment API: HTTP {Status}", (int)resp.StatusCode);
    }

    private static async Task CreateIssueAsync(
        HttpClient http, string owner, string repo, string fp,
        Exception ex, string functionName, ILogger log)
    {
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);

        var sanitizedMessage = SanitizeForPublic(ex.Message);
        var title = $"[bug][fp:{fp}] {ex.GetType().Name}: {TruncateMessage(sanitizedMessage)}";
        var body = $"## Automatisch gerapporteerde exception\n\n"
                 + $"**Tijdstip:** {nlTijd:dd-MM-yyyy HH:mm} (Europe/Amsterdam)\n"
                 + $"**Functie:** `{functionName}`\n"
                 + $"**Fingerprint:** `{fp}`\n\n"
                 + $"**Exception:** `{ex.GetType().FullName}`\n"
                 + $"**Bericht:** {sanitizedMessage}\n\n"
                 + $"**Stacktrace:**\n```\n{TruncateStackTrace(SanitizeForPublic(ex.StackTrace))}\n```\n\n"
                 + $"*Automatisch aangemaakt door GitHubIssueReporter (v2.1 zelfherstellend systeem)*";

        var payload = JsonConvert.SerializeObject(new
        {
            title,
            body,
            labels = new[] { "bug", "type: bug" }
        });

        var url = $"https://api.github.com/repos/{owner}/{repo}/issues";
        var resp = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));

        if (resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync();
            dynamic created = JsonConvert.DeserializeObject<dynamic>(json)!;
            log.LogInformation("GitHub issue #{Nr} aangemaakt voor fp:{Fp}", (int)created.number, fp);
        }
        else
        {
            log.LogWarning("GitHub issue aanmaken mislukt: HTTP {Status}", (int)resp.StatusCode);
        }
    }

    private static string TruncateStackTrace(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace)) return "(geen stacktrace)";
        var lines = stackTrace.Split('\n');
        if (lines.Length <= MaxStackTraceLines) return stackTrace.Trim();
        return string.Join('\n', lines.Take(MaxStackTraceLines)) + $"\n... ({lines.Length - MaxStackTraceLines} regels weggelaten)";
    }

    private static string TruncateMessage(string message)
    {
        if (message.Length <= 120) return message;
        return message[..117] + "...";
    }

    // Verwijdert PII en club-specifieke gegevens voordat tekst in publieke GitHub issues terechtkomt.
    // Sanitiseert: e-mailadressen, GUIDs, SQL-connectiestring-fragmenten, URL queryparameters, datums, getallen.
    private static string SanitizeForPublic(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        // URL query-parameters met gevoelige namen — clientId, code, token, key, secret (#436)
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"([?&](clientId|code|token|key|secret|apikey|client_secret|client_id)=)[^&\s""'<>]+",
            "$1<redacted>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // E-mailadressen
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", "<email>");
        // GUIDs / client IDs
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "<guid>");
        // SQL-connectiestring-fragmenten (key=value paren die credentials kunnen bevatten)
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"(?i)\b(Server|Database|User Id|Data Source|Initial Catalog|Pwd|Uid)\s*=\s*[^\s;,'""<>]+",
            "$1=<redacted>");
        // Afzonderlijke credentials (pwd, pass, secret varianten)
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"(?i)\b(pass\w*|secret\w*|token\w*|key\w*)\s*[=:]\s*\S{4,}",
            "$1=<redacted>");
        // Datums
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?)?", "<date>");
        // Losse getallen
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b\d{5,}\b", "<n>");
        return s.Trim();
    }
}
