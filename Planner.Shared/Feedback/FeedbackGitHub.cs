using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace Planner.Shared.Feedback;

/// <summary>GitHub REST-aanroepen van de feedbackwidget (#1494: afgesplitst uit FeedbackCore).</summary>
internal static class FeedbackGitHub
{
    /// <summary>
    /// Standaardimplementatie van de GitHub-issue-aanroep — geen tier-afhankelijkheid (enkel
    /// pat/owner/repo als parameters). Tier-entrypoints geven dit door als de
    /// <c>maakGitHubIssueAsync</c>-delegate aan <see cref="SubmitAsync"/>; tests injecteren daar een
    /// fake in plaats van deze methode.
    /// </summary>
    internal static async Task<(int nummer, string url)> MaakGitHubIssueAsync(
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

    private static readonly HttpClient StatusHttp = MaakStatusHttp();

    private static HttpClient MaakStatusHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SportlinkFeedbackWidget/2.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    /// <summary>
    /// Leest uit GitHub of een gepubliceerd issue gesloten is (#764, bewaartermijn: identiteit blijft
    /// bewaard zolang het issue open is plus 24 maanden na sluiting). Geeft <c>null</c> voor "nog open"
    /// en <c>false</c> in <c>gelukt</c> als de status niet te bepalen was — dan wordt er niets
    /// geanonimiseerd; liever te lang bewaren dan op een gok wissen.
    /// </summary>
    internal static async Task<(bool gelukt, DateTime? geslotenOpUtc)> HaalIssueSluitingAsync(
        string pat, string owner, string repo, int nummer, ILogger log)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/issues/{nummer}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        using var resp = await StatusHttp.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("GitHub-issuestatus ophalen mislukt voor #{Nr}: HTTP {Status}", nummer, (int)resp.StatusCode);
            return (false, null);
        }
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        if (!string.Equals(json["state"]?.Value<string>(), "closed", StringComparison.OrdinalIgnoreCase))
            return (true, null);
        var gesloten = json["closed_at"]?.Value<DateTime>();
        return (true, gesloten?.ToUniversalTime());
    }
}
