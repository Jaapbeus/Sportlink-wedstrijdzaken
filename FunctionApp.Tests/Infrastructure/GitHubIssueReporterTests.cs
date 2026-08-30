using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using SportlinkFunction.Infrastructure;
using Xunit;

namespace FunctionApp.Tests.Infrastructure;

/// <summary>
/// Tests voor GitHubIssueReporter.SearchIssueAsync — regressietest voor #830.
///
/// #830 constateerde dat een recidiverende fingerprint altijd een nieuw GitHub issue kreeg in
/// plaats van een comment op het bestaande, omdat de (inmiddels vervangen) GitHub Search API bij
/// een fine-grained PAT met alleen issues:write-scope onbetrouwbaar bleek (403/404) en de code
/// die fout stilzwijgend liet terugvallen op "nieuw issue aanmaken". De fix vervangt de Search
/// API-aanroep door de gewone Issues List API + lokale titel-matching op de fingerprint-tag.
/// </summary>
public class GitHubIssueReporterTests
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Fingerprint = "8e64c2440409";

    private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => respond(req));
        return new HttpClient(handler.Object);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    };

    private static string IssueJson(int number, string state, string title, bool isPullRequest = false)
    {
        var prField = isPullRequest ? "\"pull_request\": { \"url\": \"https://api.github.com/x\" }," : "";
        return $$"""
            {
                "number": {{number}},
                "state": "{{state}}",
                "title": {{System.Text.Json.JsonSerializer.Serialize(title)}},
                {{prField}}
                "body": "irrelevant"
            }
            """;
    }

    [Fact]
    public async Task SearchIssueAsync_OpenIssueMetFingerprintGevonden_RetourneertNummerEnNietGesloten()
    {
        // Precies het scenario uit #830: dezelfde fingerprint komt opnieuw voor. De dedup moet
        // het bestaande open issue vinden zodat ReportAsync een comment plaatst i.p.v. een
        // nieuw issue aan te maken.
        var title = $"[bug][fp:{Fingerprint}] InvalidOperationException: iets ging mis";
        var client = MakeClient(_ => JsonResponse($"[{IssueJson(370, "open", title)}]"));

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().NotBeNull("een bestaand open issue met dezelfde fingerprint moet gevonden worden i.p.v. een duplicaat te forceren");
        result!.Value.number.Should().Be(370);
        result.Value.isClosed.Should().BeFalse();
    }

    [Fact]
    public async Task SearchIssueAsync_GeslotenIssueMetFingerprintGevonden_RetourneertIsClosedTrue()
    {
        // De fout is opnieuw opgetreden nadat het issue ooit gesloten werd — ReportAsync moet dit
        // issue heropenen + becommentariëren, niet blindelings een nieuw issue aanmaken.
        var title = $"[bug][fp:{Fingerprint}] InvalidOperationException: iets ging mis";
        var client = MakeClient(_ => JsonResponse($"[{IssueJson(799, "closed", title)}]"));

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().NotBeNull();
        result!.Value.number.Should().Be(799);
        result.Value.isClosed.Should().BeTrue();
    }

    [Fact]
    public async Task SearchIssueAsync_GeenEnkelIssueMatcht_RetourneertNull()
    {
        var andereTitle = "[bug][fp:aaaaaaaaaaaa] Iets heel anders";
        var client = MakeClient(_ => JsonResponse($"[{IssueJson(1, "open", andereTitle)}]"));

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().BeNull("geen enkel issue bevat de fingerprint-tag, dus mag een nieuw issue aangemaakt worden");
    }

    [Fact]
    public async Task SearchIssueAsync_SlaatPullRequestsOver()
    {
        // De Issues List API bevat ook pull requests; een PR-titel die toevallig de tag bevat
        // (bijv. deze PR zelf) mag nooit als "bestaand issue" worden aangezien.
        var title = $"[bug][fp:{Fingerprint}] PR-titel die de tag bevat";
        var prJson = IssueJson(900, "open", title, isPullRequest: true);
        var client = MakeClient(_ => JsonResponse($"[{prJson}]"));

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().BeNull("pull requests moeten worden overgeslagen bij de fingerprint-lookup");
    }

    [Fact]
    public async Task SearchIssueAsync_HttpFout_RetourneertNullEnCreeertGeenExceptie()
    {
        // Dit is het pad dat #830 veroorzaakte: als de lookup faalt, valt ReportAsync terug op
        // "nieuw issue aanmaken". Dat blijft zo als de Issues List API zelf onbeschikbaar is —
        // maar in tegenstelling tot #830 is dat nu de uitzondering, niet de normale flow, omdat
        // de Issues List API (in tegenstelling tot de Search API) wél werkt met een PAT die
        // alleen issues:write-scope heeft.
        var client = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchIssueAsync_Netwerkfout_RetourneertNull()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Netwerk onbeschikbaar"));
        var client = new HttpClient(handler.Object);

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchIssueAsync_MatchOpTweedePagina_RetourneertNummer()
    {
        // Bewijst dat de paginering daadwerkelijk doorzoekt: pagina 1 is "vol" (100 issues, geen
        // match), pagina 2 bevat de match.
        var noise = string.Join(",", Enumerable.Range(1, 100)
            .Select(i => IssueJson(i, "open", $"[bug][fp:{i:x12}] ruis")));
        var matchTitle = $"[bug][fp:{Fingerprint}] gevonden op pagina 2";

        var client = MakeClient(req =>
        {
            var url = req.RequestUri!.ToString();
            return url.Contains("page=2")
                ? JsonResponse($"[{IssueJson(555, "open", matchTitle)}]")
                : JsonResponse($"[{noise}]");
        });

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().NotBeNull("de lookup moet doorpagineren tot de fingerprint gevonden wordt");
        result!.Value.number.Should().Be(555);
    }
}
