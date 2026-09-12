using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SportlinkFunction.Infrastructure;

/// <summary>
/// Rapporteert onverwachte exceptions als GitHub Issues.
/// Deduplicatie op fingerprint (zie SystemUtilities.ComputeFingerprint):
///   - Bestaand open issue → voeg comment toe
///   - Bestaand gesloten issue → heropen + voeg comment toe (de fout is opnieuw opgetreden)
///   - Geen bestaand issue → maak nieuw issue aan
///   - Al gerapporteerd binnen 24u → overslaan (rate-limiting, issue #106)
///
/// De dedup-lookup (<see cref="SearchIssueAsync"/>) gebruikt de gewone Issues List API
/// (<c>GET /repos/{owner}/{repo}/issues</c>), niet de GitHub Search API. De Search API bleek
/// bij een fine-grained PAT met alleen <c>issues:write</c>-scope onbetrouwbaar (403/404), wat
/// de dedup stilzwijgend liet terugvallen op "altijd een nieuw issue aanmaken" — zie #830.
///
/// Vereiste environment variables:
///   GitHubPat   — fine-grained PAT met issues:write scope (zie #103)
///   GitHubOwner — GitHub organisatie of gebruikersnaam (default: env GITHUB_REPOSITORY_OWNER)
///   GitHubRepo  — repository naam (verplicht — geen fallback, zie #607)
///
/// Wanneer GitHubPat niet geconfigureerd is, wordt alles stil overgeslagen.
/// </summary>
public static class GitHubIssueReporter
{
    private static readonly Dictionary<string, DateTime> _recentlyReported = new();
    private static readonly object _lock = new();

    private const int RateLimitHours = 24;

    public static async Task ReportAsync(Exception ex, string functionName, ILogger log)
    {
        if (!EgressGuard.ExternalIntegrationsAllowed())
        {
            log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — issue-rapportage overgeslagen (#857).");
            return;
        }

        var pat = Environment.GetEnvironmentVariable("GitHubPat");
        if (string.IsNullOrWhiteSpace(pat))
        {
            log.LogDebug("GitHubPat niet geconfigureerd — exception-reporting naar GitHub overgeslagen");
            return;
        }

        var owner = Environment.GetEnvironmentVariable("GitHubOwner")
                 ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER")
                 ?? "";
        // Geen stille fallback op de upstream-repo-naam: een fork met een andere naam zou dan
        // issues naar de verkeerde repo proberen te sturen (404). (#607)
        var repo = Environment.GetEnvironmentVariable("GitHubRepo");

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            log.LogWarning("GitHubOwner/GitHubRepo niet volledig geconfigureerd — issue-reporting overgeslagen");
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
                await AddCommentAsync(http, owner, repo, existing.Value.number, ex, functionName, fp, log);
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

    // Bovengrens aan het aantal pagina's dat doorzocht wordt — voorkomt onbegrensd doorbladeren
    // bij een repo met veel 'bug'-gelabelde issues. 5 pagina's × 100 = 500 issues.
    private const int SearchMaxPages = 5;
    private const int SearchPerPage = 100;

    /// <summary>
    /// Zoekt een bestaand issue (open of gesloten) met de fingerprint-tag <c>[fp:{fp}]</c> in de
    /// titel. Gebruikt bewust de Issues List API in plaats van de GitHub Search API — zie #830
    /// voor de reden (Search API onbetrouwbaar met een fine-grained PAT die alleen
    /// <c>issues:write</c>-scope heeft).
    /// <c>internal</c> zodat FunctionApp.Tests deze dedup-lookup rechtstreeks kan afdekken
    /// (InternalsVisibleTo, zie #476) zonder de publieke <see cref="ReportAsync"/>-signatuur
    /// te hoeven verbouwen.
    /// </summary>
    internal static async Task<(int number, bool isClosed)?> SearchIssueAsync(
        HttpClient http, string owner, string repo, string fp, ILogger log)
    {
        var marker = $"[fp:{fp}]";

        for (var page = 1; page <= SearchMaxPages; page++)
        {
            // state=all → doorzoekt open én gesloten issues, zodat een eerder gesloten issue
            // (de fout is opnieuw opgetreden) ook gevonden wordt in plaats van een duplicaat aan
            // te maken. labels=bug beperkt de resultaten tot issues die deze reporter zelf
            // aanmaakt (zie CreateIssueAsync).
            var url = "https://api.github.com/repos/" + owner + "/" + repo + "/issues"
                    + $"?state=all&labels=bug&per_page={SearchPerPage}&page={page}&sort=created&direction=desc";

            HttpResponseMessage resp;
            try
            {
                resp = await http.GetAsync(url);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "GitHub issues-lijst API: netwerkfout bij opzoeken fp:{Fp}", fp);
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("GitHub issues-lijst API: HTTP {Status} bij opzoeken fp:{Fp}", (int)resp.StatusCode, fp);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var items = JArray.Parse(json);
            if (items.Count == 0) break; // geen resultaten meer

            foreach (var token in items)
            {
                if (token is not JObject item) continue;
                // De issues-lijst bevat ook pull requests; die hebben een 'pull_request'-veld.
                if (item["pull_request"] != null) continue;

                var title = item["title"]?.Value<string>();
                if (title == null || !title.Contains(marker, StringComparison.Ordinal)) continue;

                var number = item["number"]!.Value<int>();
                var state = item["state"]!.Value<string>();
                var isClosed = state == "closed";
                log.LogInformation("Bestaand GitHub issue #{Nr} ({State}) gevonden voor fp:{Fp}", number, state, fp);
                return (number, isClosed);
            }

            if (items.Count < SearchPerPage) break; // laatste pagina bereikt
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

    /// <summary>
    /// Reageert op een recidiverende exception. <c>internal</c> zodat FunctionApp.Tests de
    /// daadwerkelijk verzonden comment-body kan afdekken (InternalsVisibleTo, zie #476) —
    /// regressietest voor #1008: de body mag nooit vrije <c>ex.Message</c>/stacktrace-tekst
    /// bevatten, alleen de vaste allowlist-velden uit <see cref="BuildPublicDiagnostics"/>.
    /// </summary>
    internal static async Task AddCommentAsync(
        HttpClient http, string owner, string repo, int issueNumber,
        Exception ex, string functionName, string fp, ILogger log)
    {
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);

        var body = "🔁 Opnieuw opgetreden\n\n" + BuildPublicDiagnostics(ex, functionName, fp, nlTijd);

        var payload = JsonConvert.SerializeObject(new { body });
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}/comments";
        var resp = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));

        if (resp.IsSuccessStatusCode)
            log.LogInformation("Comment toegevoegd aan GitHub issue #{Nr}", issueNumber);
        else
            log.LogWarning("GitHub comment API: HTTP {Status}", (int)resp.StatusCode);
    }

    /// <summary>
    /// Maakt een nieuw issue aan. <c>internal</c> zodat FunctionApp.Tests de daadwerkelijk
    /// verzonden issue-titel/body kan afdekken (InternalsVisibleTo, zie #476) — regressietest
    /// voor #1008: titel en body mogen nooit vrije <c>ex.Message</c>/stacktrace-tekst bevatten,
    /// alleen de vaste allowlist-velden uit <see cref="BuildPublicDiagnostics"/>.
    /// </summary>
    internal static async Task CreateIssueAsync(
        HttpClient http, string owner, string repo, string fp,
        Exception ex, string functionName, ILogger log)
    {
        var nlZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var nlTijd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, nlZone);

        var title = BuildPublicTitle(ex, fp);
        var body = "## Automatisch gerapporteerde exception\n\n"
                 + BuildPublicDiagnostics(ex, functionName, fp, nlTijd)
                 + "\n\n*Automatisch aangemaakt door GitHubIssueReporter (v2.1 zelfherstellend systeem)*";

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

    /// <summary>
    /// Bouwt de publieke issue-titel. Bevat uitsluitend het exceptietype en de fingerprint-tag —
    /// nooit <c>ex.Message</c> (#1008: vrije foutteksten kunnen databasenamen, servernamen of
    /// andere identificerende inhoud bevatten die geen enkel bestaand denylist-patroon dekt).
    /// <c>internal</c> zodat FunctionApp.Tests dit rechtstreeks kan afdekken (InternalsVisibleTo, #476).
    /// </summary>
    internal static string BuildPublicTitle(Exception ex, string fp)
        => $"[bug][fp:{fp}] {ClassifyErrorCategory(ex)}: {ex.GetType().Name}";

    /// <summary>
    /// Bouwt het publieke diagnostiek-blok volgens het allowlist-model uit #1008: uitsluitend
    /// vaste technische velden (foutcategorie, exceptietype, interne operationele naam — de
    /// Azure Function-naam — veilige fingerprint/hash, tijdstip). Vrije <c>ex.Message</c>,
    /// inner-exceptietekst en bronpaden/stacktrace worden NOOIT overgenomen — die blijven
    /// uitsluitend in de structured logging/Application Insights van deze Function App
    /// (zie de <c>log.LogError(ex, ...)</c>-aanroep vóór <see cref="ReportAsync"/> in
    /// Function1.cs), nooit in dit publieke GitHub-issue.
    /// <c>internal</c> zodat FunctionApp.Tests dit rechtstreeks kan afdekken (InternalsVisibleTo, #476).
    /// </summary>
    internal static string BuildPublicDiagnostics(Exception ex, string functionName, string fp, DateTime nlTijd)
    {
        var innerType = ex.InnerException?.GetType().FullName ?? "(geen)";
        return $"**Foutcategorie:** {ClassifyErrorCategory(ex)}\n"
             + $"**Exceptietype:** `{ex.GetType().FullName}`\n"
             + $"**Inner exceptietype:** `{innerType}`\n"
             + $"**Interne operationele naam:** `{functionName}`\n"
             + $"**Fingerprint:** `{fp}`\n"
             + $"**Tijdstip:** {nlTijd:dd-MM-yyyy HH:mm} (Europe/Amsterdam)\n\n"
             + "*Volledige diagnostiek (foutbericht, stacktrace, bronpaden) staat uitsluitend in de "
             + "structured logging/Application Insights van deze Function App — nooit in dit publieke issue.*";
    }

    /// <summary>
    /// Classificeert een exception (incl. inner exceptions) naar een vaste, veilige categorie —
    /// onderdeel van het allowlist-model van #1008. Doorloopt de inner-exceptionketen zodat een
    /// gewrapte SQL-fout (bv. via een repository-laag) ook als "Database" herkend wordt.
    /// </summary>
    private static string ClassifyErrorCategory(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var typeName = current.GetType().FullName ?? "";
            if (typeName.Contains("Sql", StringComparison.OrdinalIgnoreCase))
                return "Database";
            if (current is TimeoutException or TaskCanceledException or OperationCanceledException)
                return "Timeout";
            if (current is HttpRequestException or System.Net.WebException or System.Net.Sockets.SocketException)
                return "Netwerk";
            if (current is InvalidOperationException or ArgumentException or FormatException)
                return "Configuratie/status";
        }
        return "Onbekend";
    }
}
