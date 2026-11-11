using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Planner.Shared.Feedback;

/// <summary>GitHub REST-aanroepen van de feedbackwidget (#1494: afgesplitst uit FeedbackCore).</summary>
internal static class FeedbackGitHub
{
    // GitHub documents a 10-second REST processing window; 15 seconds allows response transit/parsing
    // while bounding client waits (https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api#timeouts).
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    internal static HttpClient MaakClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = timeout ?? RequestTimeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SportlinkFeedbackWidget/2.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    internal static async Task<(int nummer, string url)> MaakGitHubIssueAsync(
        string pat, string owner, string repo, string title, string body,
        string[] labels, ILogger log, CancellationToken cancellationToken = default,
        HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        using var http = MaakClient(handler, timeout);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);

        var payload = JsonConvert.SerializeObject(new { title, body, labels });
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues";
        var resp = await VerstuurCreateAsync(http, url, payload, cancellationToken);

        using (resp)
        {
            if ((int)resp.StatusCode == 422)
            {
                // Een expliciete 422 is de enige veilige reden voor de bestaande label-fallback.
                log.LogWarning("GitHub 422 bij labels {Labels} — retry zonder custom labels", string.Join(",", labels));
                var fallbackLabels = labels.Where(l => l == "bug" || l == "enhancement" || l == "question").ToArray();
                var fallbackPayload = JsonConvert.SerializeObject(new { title, body, labels = fallbackLabels });
                using var fallback = await VerstuurCreateAsync(http, url, fallbackPayload, cancellationToken);
                if (IsAmbigueWriteResponse(fallback.StatusCode))
                    throw new FeedbackPublicatieUitkomstOnbekendException(new HttpRequestException($"GitHub API HTTP {(int)fallback.StatusCode}", null, fallback.StatusCode));
                if (!fallback.IsSuccessStatusCode)
                {
                    log.LogWarning("GitHub issue aanmaken mislukt: HTTP {Status}", (int)fallback.StatusCode);
                    throw new InvalidOperationException($"GitHub API HTTP {(int)fallback.StatusCode}");
                }
                return await LeesAangemaaktIssueAsync(fallback, log, cancellationToken);
            }

            if (IsAmbigueWriteResponse(resp.StatusCode))
                throw new FeedbackPublicatieUitkomstOnbekendException(new HttpRequestException($"GitHub API HTTP {(int)resp.StatusCode}", null, resp.StatusCode));
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("GitHub issue aanmaken mislukt: HTTP {Status}", (int)resp.StatusCode);
                throw new InvalidOperationException($"GitHub API HTTP {(int)resp.StatusCode}");
            }
            return await LeesAangemaaktIssueAsync(resp, log, cancellationToken);
        }
    }

    private static bool IsAmbigueWriteResponse(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout || (int)statusCode >= 500;

    private static async Task<HttpResponseMessage> VerstuurCreateAsync(HttpClient http, string url, string payload, CancellationToken cancellationToken)
    {
        // Echte pre-send annulering is veilig te herhalen: controleer vóór de transportaanroep.
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            throw new FeedbackPublicatieUitkomstOnbekendException(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new FeedbackPublicatieUitkomstOnbekendException(ex);
        }
    }

    private static async Task<(int nummer, string url)> LeesAangemaaktIssueAsync(HttpResponseMessage resp, ILogger log, CancellationToken cancellationToken)
    {
        try
        {
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            dynamic created = JsonConvert.DeserializeObject<dynamic>(json)!;
            int nummer = (int)created.number;
            string issueUrl = (string)created.html_url;
            log.LogInformation("GitHub issue #{Nr} aangemaakt via feedback widget", nummer);
            return (nummer, issueUrl);
        }
        catch (Exception ex)
        {
            // Een 2xx antwoord heeft al bevestigd dat GitHub de write accepteerde; ontbrekende of
            // onleesbare responsegegevens mogen daarom ook geen herhaalbare fout worden.
            throw new FeedbackPublicatieUitkomstOnbekendException(ex);
        }
    }

    private static readonly HttpClient StatusHttp = MaakClient();

    /// <summary>
    /// Leest uit GitHub of een gepubliceerd issue gesloten is. Een fout geeft fail-closed terug,
    /// zodat een onbekende status nooit de retentie-anonimisering activeert.
    /// </summary>
    internal static async Task<(bool gelukt, DateTime? geslotenOpUtc)> HaalIssueSluitingAsync(
        string pat, string owner, string repo, int nummer, ILogger log, CancellationToken cancellationToken = default,
        HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/issues/{nummer}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        using var injectedClient = handler is null ? null : MaakClient(handler, timeout);
        try
        {
            using var resp = await (injectedClient ?? StatusHttp).SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("GitHub-issuestatus ophalen mislukt voor #{Nr}: HTTP {Status}", nummer, (int)resp.StatusCode);
                return (false, null);
            }
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            if (!string.Equals(json["state"]?.Value<string>(), "closed", StringComparison.OrdinalIgnoreCase))
                return (true, null);
            var gesloten = json["closed_at"]?.Value<DateTime>();
            return (true, gesloten?.ToUniversalTime());
        }
        catch (OperationCanceledException ex)
        {
            log.LogWarning("GitHub-issuestatus ophalen geannuleerd voor #{Nr}; status blijft onbekend ({ErrorType})", nummer, ex.GetType().Name);
            return (false, null);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("GitHub-issuestatus ophalen mislukt voor #{Nr}; status blijft onbekend ({ErrorType})", nummer, ex.GetType().Name);
            return (false, null);
        }
    }
}
