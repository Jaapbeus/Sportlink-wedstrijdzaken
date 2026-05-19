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
        var repo = Environment.GetEnvironmentVariable("GitHubRepo") ?? "Sportlink-wedstrijdzaken";

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
                await AddCommentAsync(http, owner, repo, existing.Value, ex, functionName, log);
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

    private static async Task<int?> SearchIssueAsync(
        HttpClient http, string owner, string repo, string fp, ILogger log)
    {
        var query = Uri.EscapeDataString($"[fp:{fp}] in:title repo:{owner}/{repo} is:open");
        var url = $"https://api.github.com/search/issues?q={query}&per_page=1";
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
            log.LogInformation("Bestaand GitHub issue #{Nr} gevonden voor fp:{Fp}", number, fp);
            return number;
        }
        return null;
    }

    private static async Task AddCommentAsync(
        HttpClient http, string owner, string repo, int issueNumber,
        Exception ex, string functionName, ILogger log)
    {
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);

        var body = $"🔁 Opnieuw opgetreden op {nlTijd:dd-MM-yyyy HH:mm} in functie `{functionName}`\n\n"
                 + $"**Exception:** `{ex.GetType().FullName}: {ex.Message}`\n\n"
                 + $"**Stacktrace:**\n```\n{TruncateStackTrace(ex.StackTrace)}\n```";

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

        var title = $"[bug][fp:{fp}] {ex.GetType().Name}: {TruncateMessage(ex.Message)}";
        var body = $"## Automatisch gerapporteerde exception\n\n"
                 + $"**Tijdstip:** {nlTijd:dd-MM-yyyy HH:mm} (Europe/Amsterdam)\n"
                 + $"**Functie:** `{functionName}`\n"
                 + $"**Fingerprint:** `{fp}`\n\n"
                 + $"**Exception:** `{ex.GetType().FullName}`\n"
                 + $"**Bericht:** {ex.Message}\n\n"
                 + $"**Stacktrace:**\n```\n{TruncateStackTrace(ex.StackTrace)}\n```\n\n"
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
}
