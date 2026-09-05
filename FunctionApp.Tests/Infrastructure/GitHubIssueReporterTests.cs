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

    // ---------------------------------------------------------------------------------------
    // Regressietests voor #1008: allowlist i.p.v. denylist voor publieke exception-rapportage.
    //
    // De oorspronkelijke SanitizeForPublic-denylist redigeerde key/value-vormen (bv.
    // "Database=..."), maar niet een databasenaam die in een natuurlijke SQL-foutzin voorkomt
    // (bv. "Cannot open database ""X"" requested by the login"). De fix vervangt vrije
    // ex.Message/stacktrace-publicatie volledig door een vaste allowlist van technische velden
    // (BuildPublicTitle/BuildPublicDiagnostics). Deze tests bewijzen dat de UITEINDELIJK
    // gepubliceerde titel/body — inclusief de daadwerkelijk naar de GitHub API verzonden
    // request-body — de synthetische marker nergens bevat, ongeacht welke zinsvorm de
    // onderliggende SQL-foutmelding gebruikt.
    // ---------------------------------------------------------------------------------------

    /// <summary>Test-only exception waarvan de typenaam "Sql" bevat, ter simulatie van een
    /// (in productie niet-instantieerbare) Microsoft.Data.SqlClient.SqlException.</summary>
    private sealed class SyntheticSqlLikeException : Exception
    {
        public SyntheticSqlLikeException(string message, Exception? inner = null) : base(message, inner) { }
    }

    // Synthetische CREATE TABLE-permissionfout met een databasenaam-marker in de foutzin —
    // exact het scenario uit het issue: "CREATE TABLE permission denied in database 'X'."
    private const string CreateTablePermissionMarker = "MARKER_DB_9f3c2b1a_ACME";
    private static readonly Exception CreateTablePermissionException = new SyntheticSqlLikeException(
        $"CREATE TABLE permission denied in database '{CreateTablePermissionMarker}'. " +
        "The user does not have permission to perform this action on schema 'stg'.");

    // Synthetische database/login-fout met zowel een databasenaam- als een login-marker.
    private const string DatabaseLoginDbMarker = "MARKER_DB_54321_CLUB";
    private const string DatabaseLoginUserMarker = "MARKER_LOGIN_abcde12345";
    private static readonly Exception DatabaseLoginException = new SyntheticSqlLikeException(
        $"Login failed for user '{DatabaseLoginUserMarker}'. Cannot open database \"{DatabaseLoginDbMarker}\" " +
        "requested by the login. The login failed.");

    public static IEnumerable<object[]> SyntheticSqlFoutvormen()
    {
        yield return new object[] { CreateTablePermissionException, new[] { CreateTablePermissionMarker } };
        yield return new object[] { DatabaseLoginException, new[] { DatabaseLoginDbMarker, DatabaseLoginUserMarker } };
    }

    [Theory]
    [MemberData(nameof(SyntheticSqlFoutvormen))]
    public void BuildPublicTitle_SyntheticSqlFout_BevatMarkerNergens(Exception ex, string[] markers)
    {
        var title = GitHubIssueReporter.BuildPublicTitle(ex, Fingerprint);

        foreach (var marker in markers)
            title.Should().NotContain(marker, "de titel mag nooit vrije ex.Message-tekst bevatten (#1008)");
    }

    [Theory]
    [MemberData(nameof(SyntheticSqlFoutvormen))]
    public void BuildPublicDiagnostics_SyntheticSqlFout_BevatMarkerNergensMaarWelAllowlistVelden(Exception ex, string[] markers)
    {
        var diagnostics = GitHubIssueReporter.BuildPublicDiagnostics(ex, "FetchAndStoreApiData", Fingerprint, DateTime.Now);

        foreach (var marker in markers)
            diagnostics.Should().NotContain(marker, "het diagnostiek-blok mag nooit vrije ex.Message-tekst bevatten (#1008)");

        // De allowlist-velden moeten wél aanwezig zijn — dit is geen "verwijder alles"-fix.
        diagnostics.Should().Contain("Foutcategorie");
        diagnostics.Should().Contain("Exceptietype");
        diagnostics.Should().Contain("Interne operationele naam");
        diagnostics.Should().Contain("FetchAndStoreApiData");
        diagnostics.Should().Contain("Fingerprint");
        diagnostics.Should().Contain(Fingerprint);
    }

    [Theory]
    [MemberData(nameof(SyntheticSqlFoutvormen))]
    public async Task CreateIssueAsync_SyntheticSqlFout_VerzondenRequestBodyBevatMarkerNergens(Exception ex, string[] markers)
    {
        // Bewijst de daadwerkelijk naar de GitHub API verzonden request-body (title + body),
        // niet alleen de geïsoleerde builder-functie.
        string? capturedRequestBody = null;
        var client = MakeClient(req =>
        {
            capturedRequestBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"number": 4242}""");
        });

        await GitHubIssueReporter.CreateIssueAsync(
            client, Owner, Repo, Fingerprint, ex, "FetchAndStoreApiData", NullLogger.Instance);

        capturedRequestBody.Should().NotBeNull();
        foreach (var marker in markers)
            capturedRequestBody.Should().NotContain(marker, "de verzonden issue-titel/body mag de synthetische marker nooit bevatten (#1008)");
        capturedRequestBody.Should().Contain(Fingerprint);
    }

    [Theory]
    [MemberData(nameof(SyntheticSqlFoutvormen))]
    public async Task AddCommentAsync_SyntheticSqlFout_VerzondenRequestBodyBevatMarkerNergens(Exception ex, string[] markers)
    {
        // Zelfde beleid als CreateIssueAsync moet gelden voor het heropeningen-/comment-pad —
        // het issue eist expliciet dat alle drie de paden (nieuw issue, heropening, comment)
        // hetzelfde beleid volgen.
        string? capturedRequestBody = null;
        var client = MakeClient(req =>
        {
            capturedRequestBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        await GitHubIssueReporter.AddCommentAsync(
            client, Owner, Repo, 370, ex, "FetchAndStoreApiData", Fingerprint, NullLogger.Instance);

        capturedRequestBody.Should().NotBeNull();
        foreach (var marker in markers)
            capturedRequestBody.Should().NotContain(marker, "de verzonden comment-body mag de synthetische marker nooit bevatten (#1008)");
        capturedRequestBody.Should().Contain(Fingerprint);
    }

    [Fact]
    public void BuildPublicTitle_SqlAchtigeException_Classificeert_Als_Database()
    {
        // Categorisatie is onderdeel van de allowlist (#1008) — geen vrije tekst, wel een vast,
        // veilig ingedeeld foutcategorie-veld.
        var title = GitHubIssueReporter.BuildPublicTitle(CreateTablePermissionException, Fingerprint);

        title.Should().Contain("Database");
        title.Should().Contain(nameof(SyntheticSqlLikeException));
    }

    [Fact]
    public void BuildPublicDiagnostics_InnerException_PubliceertAlleenInnerExceptietype()
    {
        // Ook de inner-exceptietekst (niet alleen de buitenste ex.Message) mag niet vrij
        // gepubliceerd worden — alleen het inner-exceptietype (.NET-klassenaam) is toegestaan.
        const string innerMarker = "MARKER_INNER_TEKST_should_never_leak";
        var inner = new InvalidOperationException($"Interne foutdetail met {innerMarker}");
        var outer = new SyntheticSqlLikeException("Buitenste fout zonder marker", inner);

        var diagnostics = GitHubIssueReporter.BuildPublicDiagnostics(outer, "FetchAndStoreApiData", Fingerprint, DateTime.Now);

        diagnostics.Should().NotContain(innerMarker);
        diagnostics.Should().Contain(nameof(InvalidOperationException));
    }
}
