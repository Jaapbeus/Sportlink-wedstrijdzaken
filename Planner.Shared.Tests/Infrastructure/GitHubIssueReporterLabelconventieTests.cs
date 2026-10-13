using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Newtonsoft.Json.Linq;
using Planner.Shared.Infrastructure;
using Xunit;

namespace Planner.Shared.Tests.Infrastructure;

/// <summary>
/// Labelconventie, opdrachtregel en deduplicatie van automatisch gemelde exceptions (#1711).
///
/// <para>
/// Aanleiding: #1709 kreeg van de reporter de labels <c>bug</c> + <c>type: bug</c>, maar geen
/// <c>priority:</c>, geen herkomst en geen vastgelegde opdracht. Een nieuw issue krijgt nu precies
/// één <c>type:</c>, één <c>priority:</c> en het herkomstlabel <c>via: auto-exception</c>, en direct
/// daarna een vaste opdrachtcomment. De dedup-lookup filterde op <c>labels=bug</c>; zonder dat label
/// op nieuwe issues zou elke herhaling een duplicaat opleveren. Hij zoekt nu op het herkomstlabel,
/// op het oude label en tot slot zonder labelfilter, steeds op de fingerprint in de titel.
/// </para>
/// </summary>
public class GitHubIssueReporterLabelconventieTests
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Fingerprint = "1711aa1711bb";
    private const string Treffertitel = "[bug][fp:" + Fingerprint + "] Configuratie/status: FormatException";

    // ---------------------------------------------------------------------------------------
    // Labels bij aanmaken
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateIssueAsync_VerstuurtPreciesDeConventielabels()
    {
        var verzoeken = new List<(HttpMethod Methode, string Pad, string Body)>();
        var client = MaakClient(verzoeken, _ => Json("""{"number": 1709}"""));

        await GitHubIssueReporter.CreateIssueAsync(
            client, Owner, Repo, Fingerprint, new FormatException("x"), "PostgresSyncMatchesHttp", NullLogger.Instance);

        var labels = LabelsVanAanmaakverzoek(verzoeken);
        labels.Should().BeEquivalentTo(new[] { "type: bug", "priority: medium", "via: auto-exception" },
            "CONTRIBUTING.md: precies één type:, één priority: en één herkomstlabel");
    }

    [Fact]
    public async Task CreateIssueAsync_GeenOudBugLabelGeenStatusEnGeenTurn()
    {
        var verzoeken = new List<(HttpMethod Methode, string Pad, string Body)>();
        var client = MaakClient(verzoeken, _ => Json("""{"number": 1709}"""));

        await GitHubIssueReporter.CreateIssueAsync(
            client, Owner, Repo, Fingerprint, new FormatException("x"), "PostgresSyncMatchesHttp", NullLogger.Instance);

        var labels = LabelsVanAanmaakverzoek(verzoeken);
        labels.Should().NotContain("bug", "het oude label 'bug' staat dubbel naast de conventie 'type: bug'");
        labels.Should().NotContain(l => l.StartsWith("status:"), "de automatiek zet de status (triage), niet de aanmaker");
        labels.Should().NotContain(l => l.StartsWith("turn:"),
            "turn: claude-code start de CI-bot als tweede implementer (#1692/#1702)");
        labels.Count(l => l.StartsWith("type:")).Should().Be(1);
        labels.Count(l => l.StartsWith("priority:")).Should().Be(1);
        labels.Count(l => l.StartsWith("source:") || l.StartsWith("via:")).Should().Be(1, "precies één herkomst");
    }

    // ---------------------------------------------------------------------------------------
    // Opdrachtregel na het aanmaken
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateIssueAsync_PlaatstNaAanmakenDeVasteOpdrachtcomment()
    {
        var verzoeken = new List<(HttpMethod Methode, string Pad, string Body)>();
        var client = MaakClient(verzoeken, _ => Json("""{"number": 1709}"""));

        await GitHubIssueReporter.CreateIssueAsync(
            client, Owner, Repo, Fingerprint, new FormatException("x"), "PostgresSyncMatchesHttp", NullLogger.Instance);

        verzoeken.Should().HaveCount(2, "eerst het issue, daarna de opdrachtcomment");
        var comment = verzoeken[1];
        comment.Methode.Should().Be(HttpMethod.Post);
        comment.Pad.Should().Be($"/repos/{Owner}/{Repo}/issues/1709/comments");
        var tekst = JObject.Parse(comment.Body)["body"]!.Value<string>();
        tekst.Should().Be(GitHubIssueReporter.OpdrachtRegel);
        tekst.Should().Contain("implementer Claude Code").And.Contain("reviewer Codex").And.Contain("fase triage");
    }

    [Fact]
    public async Task CreateIssueAsync_AanmakenMislukt_PlaatstGeenComment()
    {
        var verzoeken = new List<(HttpMethod Methode, string Pad, string Body)>();
        var client = MaakClient(verzoeken, _ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));

        await GitHubIssueReporter.CreateIssueAsync(
            client, Owner, Repo, Fingerprint, new FormatException("x"), "PostgresSyncMatchesHttp", NullLogger.Instance);

        verzoeken.Should().ContainSingle("zonder aangemaakt issue is er niets om de opdracht bij vast te leggen");
    }

    // ---------------------------------------------------------------------------------------
    // Deduplicatie: oude issues (label bug), nieuwe (via: auto-exception) en ongelabelde
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SearchIssueAsync_NieuwIssueZonderBugLabel_WordtGevonden()
    {
        var client = MaakClient(new(), req =>
            Query(req).Contains("labels=via%3A%20auto-exception") ? Treffer(1709) : Json("[]"));

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().NotBeNull("een nieuw issue draagt geen 'bug' meer; de dedup moet het via het herkomstlabel vinden");
        result!.Value.number.Should().Be(1709);
    }

    [Fact]
    public async Task SearchIssueAsync_OudIssueMetBugLabel_WordtNogSteedsGevonden()
    {
        var client = MaakClient(new(), req =>
            Query(req).Contains("labels=bug&") ? Treffer(370) : Json("[]"));

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().NotBeNull("issues van vóór #1711 hebben alleen het oude label en mogen geen duplicaat krijgen");
        result!.Value.number.Should().Be(370);
    }

    [Fact]
    public async Task SearchIssueAsync_IssueZonderHerkomstlabel_WordtZonderLabelfilterGevonden()
    {
        // Iemand heeft de labels van een melding gewijzigd (bijv. #1709 na livegang): het issue
        // heeft dan geen van beide labels meer, maar de fingerprint staat nog in de titel.
        var client = MaakClient(new(), req =>
            Query(req).Contains("labels=") ? Json("[]") : Treffer(1709));

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().NotBeNull();
        result!.Value.number.Should().Be(1709);
    }

    [Fact]
    public async Task SearchIssueAsync_ZoektOpenEnGeslotenEnBlijftBinnenDePaginagrens()
    {
        // Geen enkele treffer en elke pagina vol: de lookup moet stoppen bij de paginagrens per
        // zoekslag, altijd state=all gebruiken (ook gesloten issues worden heropend) en geen
        // andere labelfilters gebruiken dan herkomstlabel, oud label en geen filter.
        var verzoeken = new List<(HttpMethod Methode, string Pad, string Body)>();
        var vol = "[" + string.Join(",", Enumerable.Range(1, 100)
            .Select(i => $$"""{"number": {{i}}, "state": "open", "title": "[bug][fp:{{i:x12}}] ruis"}""")) + "]";
        var queries = new List<string>();
        var client = MaakClient(verzoeken, req => { queries.Add(Query(req)); return Json(vol); });

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().BeNull();
        queries.Should().HaveCount(15, "drie zoekslagen van elk hoogstens vijf pagina's");
        queries.Should().OnlyContain(q => q.Contains("state=all"));
        queries.Count(q => q.Contains("labels=via%3A%20auto-exception")).Should().Be(5);
        queries.Count(q => q.Contains("labels=bug&")).Should().Be(5);
        queries.Count(q => !q.Contains("labels=")).Should().Be(5);
    }

    [Fact]
    public async Task SearchIssueAsync_FoutInEersteZoekslag_StoptZonderVerdereAanroepen()
    {
        // Ongewijzigd gedrag (#830): een mislukte lookup geeft null en de aanroeper maakt een
        // nieuw issue. Geen stille doorval naar de volgende zoekslag op basis van een half beeld.
        var verzoeken = new List<(HttpMethod Methode, string Pad, string Body)>();
        var client = MaakClient(verzoeken, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await GitHubIssueReporter.SearchIssueAsync(client, Owner, Repo, Fingerprint, NullLogger.Instance);

        result.Should().BeNull();
        verzoeken.Should().ContainSingle();
    }

    // ---------------------------------------------------------------------------------------

    private static List<string> LabelsVanAanmaakverzoek(List<(HttpMethod Methode, string Pad, string Body)> verzoeken)
    {
        var aanmaak = verzoeken.Single(v => v.Pad == $"/repos/{Owner}/{Repo}/issues");
        return JObject.Parse(aanmaak.Body)["labels"]!.Values<string>().Select(l => l!).ToList();
    }

    private static string Query(HttpRequestMessage req) => req.RequestUri!.Query;

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static HttpResponseMessage Treffer(int nummer)
        => Json($$"""[{"number": {{nummer}}, "state": "open", "title": {{System.Text.Json.JsonSerializer.Serialize(Treffertitel)}}}]""");

    private static HttpClient MaakClient(
        List<(HttpMethod Methode, string Pad, string Body)> verzoeken,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                verzoeken.Add((req.Method, req.RequestUri!.AbsolutePath, body));
                return respond(req);
            });
        return new HttpClient(handler.Object);
    }
}
