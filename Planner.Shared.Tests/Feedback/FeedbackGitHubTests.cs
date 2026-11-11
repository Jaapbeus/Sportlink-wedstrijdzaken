using System.Net;
using AwesomeAssertions;
using Xunit;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Feedback;

namespace Planner.Shared.Tests.Feedback;

public sealed class FeedbackGitHubTests
{
    [Fact]
    public async Task Create_success_returns_issue_and_uses_bounded_client()
    {
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.Created, "{\"number\":12,\"html_url\":\"https://github.com/example/repo/issues/12\"}")));
        var result = await FeedbackGitHub.MaakGitHubIssueAsync("synthetic-token", "example", "repo", "title", "body", ["bug"], NullLogger.Instance,
            handler: handler, timeout: TimeSpan.FromSeconds(1));

        result.Should().Be((12, "https://github.com/example/repo/issues/12"));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Headers.UserAgent.ToString().Should().Contain("SportlinkFeedbackWidget");
    }

    [Fact]
    public async Task Create_422_retries_only_once_without_custom_labels()
    {
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.UnprocessableEntity, "{}")),
            (_, _) => Task.FromResult(Json(HttpStatusCode.Created, "{\"number\":13,\"html_url\":\"https://github.com/example/repo/issues/13\"}")));

        var result = await FeedbackGitHub.MaakGitHubIssueAsync("synthetic-token", "example", "repo", "title", "body", ["bug", "type: bug"], NullLogger.Instance,
            handler: handler, timeout: TimeSpan.FromSeconds(1));

        result.Item1.Should().Be(13);
        handler.Requests.Should().HaveCount(2);
        handler.Bodies[1].Should().Contain("\"labels\":[\"bug\"]");
    }

    [Fact]
    public async Task Create_timeout_has_unknown_outcome_and_does_not_retry()
    {
        using var timedOut = new CancellationTokenSource();
        var handler = new SequenceHandler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { timedOut.Cancel(); throw; }
            return Json(HttpStatusCode.Created, "{}");
        });

        var act = () => FeedbackGitHub.MaakGitHubIssueAsync("synthetic-token", "example", "repo", "title", "body", ["bug"], NullLogger.Instance,
            handler: handler, timeout: TimeSpan.FromMilliseconds(50));

        await act.Should().ThrowAsync<FeedbackPublicatieUitkomstOnbekendException>();
        timedOut.IsCancellationRequested.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_caller_cancellation_has_unknown_outcome_and_does_not_retry()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var handler = new SequenceHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(HttpStatusCode.Created, "{}");
        });

        var act = () => FeedbackGitHub.MaakGitHubIssueAsync("synthetic-token", "example", "repo", "title", "body", ["bug"], NullLogger.Instance,
            cts.Token, handler, TimeSpan.FromSeconds(2));

        await act.Should().ThrowAsync<FeedbackPublicatieUitkomstOnbekendException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_already_cancelled_token_stops_before_transport()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.Created, "{}")));

        var act = () => FeedbackGitHub.MaakGitHubIssueAsync("synthetic-token", "example", "repo", "title", "body", ["bug"], NullLogger.Instance,
            cts.Token, handler, TimeSpan.FromSeconds(1));

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_server_error_is_unknown_and_does_not_retry()
    {
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}")));

        var act = () => FeedbackGitHub.MaakGitHubIssueAsync("synthetic-token", "example", "repo", "title", "body", ["bug"], NullLogger.Instance,
            handler: handler, timeout: TimeSpan.FromSeconds(1));

        await act.Should().ThrowAsync<FeedbackPublicatieUitkomstOnbekendException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_success_without_readable_receipt_is_unknown_and_not_retried()
    {
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.Created, "{}")));

        var act = () => FeedbackGitHub.MaakGitHubIssueAsync("synthetic-token", "example", "repo", "title", "body", ["bug"], NullLogger.Instance,
            handler: handler, timeout: TimeSpan.FromSeconds(1));

        await act.Should().ThrowAsync<FeedbackPublicatieUitkomstOnbekendException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Status_http_error_fails_closed()
    {
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}")));

        var result = await FeedbackGitHub.HaalIssueSluitingAsync("synthetic-token", "example", "repo", 12, NullLogger.Instance,
            handler: handler, timeout: TimeSpan.FromSeconds(1));

        result.Should().Be((false, (DateTime?)null));
    }

    [Fact]
    public async Task Status_timeout_fails_closed()
    {
        var handler = new SequenceHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(HttpStatusCode.OK, "{}");
        });

        var result = await FeedbackGitHub.HaalIssueSluitingAsync("synthetic-token", "example", "repo", 12, NullLogger.Instance,
            handler: handler, timeout: TimeSpan.FromMilliseconds(50));

        result.Should().Be((false, (DateTime?)null));
        handler.Requests.Should().ContainSingle();
    }

    // ── Zoeken naar een eerder aangemaakt issue (#1803) ────────────────────────────────────────

    private const string Ref = "abcdefghijklmnopqrstuvwx";
    private static readonly DateTime Grens = new(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);
    private static string Body(string referentie, string tekst = "Tekst.") =>
        FeedbackPublicatieReferentie.VoegToe(tekst, referentie).Replace("\n", "\\n").Replace("\"", "\\\"");

    /// <summary>Een issue uit de lijst; standaard aangemaakt ná de zoekgrens.</summary>
    private static string Item(int nr, string referentie, bool isPr = false, DateTime? aangemaakt = null, string tekst = "Tekst.") =>
        $"{{\"number\":{nr},\"html_url\":\"https://github.com/example/repo/issues/{nr}\",\"created_at\":\"{aangemaakt ?? Grens.AddMinutes(10):yyyy-MM-ddTHH:mm:ssZ}\",\"body\":\"{Body(referentie, tekst)}\"{(isPr ? ",\"pull_request\":{}" : "")}}}";

    [Fact]
    public async Task Zoek_vindt_issue_via_issuelijst_en_niet_via_search_api()
    {
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, $"[{Item(5, "zzzzzzzzzzzzzzzzzzzzzzzz")},{Item(9, Ref)}]")));

        var result = await FeedbackGitHub.ZoekIssueMetReferentieAsync("synthetic-token", "example", "repo", Ref,
            new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc), NullLogger.Instance, handler: handler, timeout: TimeSpan.FromSeconds(1));

        result.gelukt.Should().BeTrue();
        result.gevonden.Should().Be(new FeedbackGevondenIssue(9, "https://github.com/example/repo/issues/9"));
        var url = handler.Requests.Single().RequestUri!.ToString();
        url.Should().Contain("/repos/example/repo/issues?").And.Contain("state=all").And.Contain("since=2026-11-11T08:00:00Z");
        url.Should().NotContain("/search/");
    }

    [Fact]
    public async Task Zoek_negeert_pull_requests_en_meldt_niet_gevonden_bij_korte_lijst()
    {
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, $"[{Item(9, Ref, isPr: true)}]")));

        var result = await FeedbackGitHub.ZoekIssueMetReferentieAsync("synthetic-token", "example", "repo", Ref,
            Grens, NullLogger.Instance, handler: handler, timeout: TimeSpan.FromSeconds(1));

        result.Should().Be((true, (FeedbackGevondenIssue?)null));
    }

    [Fact]
    public async Task Zoek_http_fout_is_onbekend_en_geen_bewijs_van_afwezigheid()
    {
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.BadGateway, "{}")));

        var result = await FeedbackGitHub.ZoekIssueMetReferentieAsync("synthetic-token", "example", "repo", Ref,
            Grens, NullLogger.Instance, handler: handler, timeout: TimeSpan.FromSeconds(1));

        result.Should().Be((false, (FeedbackGevondenIssue?)null));
    }

    [Fact]
    public async Task Zoek_timeout_is_onbekend()
    {
        var handler = new SequenceHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(HttpStatusCode.OK, "[]");
        });

        var result = await FeedbackGitHub.ZoekIssueMetReferentieAsync("synthetic-token", "example", "repo", Ref,
            Grens, NullLogger.Instance, handler: handler, timeout: TimeSpan.FromMilliseconds(50));

        result.gelukt.Should().BeFalse();
    }

    [Fact]
    public async Task Zoek_volgt_volgende_pagina_en_geeft_op_na_het_maximum_zonder_te_beweren_dat_het_issue_ontbreekt()
    {
        var vol = "[" + string.Join(",", Enumerable.Range(1, 100).Select(n => Item(n, "zzzzzzzzzzzzzzzzzzzzzzzz"))) + "]";
        var handler = new SequenceHandler(Enumerable.Range(0, 10)
            .Select(_ => (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((_, _) => Task.FromResult(Json(HttpStatusCode.OK, vol))))
            .ToArray());

        var result = await FeedbackGitHub.ZoekIssueMetReferentieAsync("synthetic-token", "example", "repo", Ref,
            Grens, NullLogger.Instance, handler: handler, timeout: TimeSpan.FromSeconds(5));

        result.gelukt.Should().BeFalse();
        handler.Requests.Should().HaveCount(10);
        handler.Requests[1].RequestUri!.Query.Should().Contain("page=2");
    }

    [Fact]
    public async Task Zoek_stopt_bij_het_eerste_issue_van_voor_de_grens_en_vraagt_geen_volgende_pagina()
    {
        // since filtert op updated_at; oudere maar recent bijgewerkte issues staan (op aanmaakdatum, nieuwste eerst) achteraan.
        var nieuw = Enumerable.Range(1, 99).Select(n => Item(200 - n, "zzzzzzzzzzzzzzzzzzzzzzzz"));
        var oud = Item(3, Ref, aangemaakt: Grens.AddMinutes(-1));
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "[" + string.Join(",", nieuw.Append(oud)) + "]")));

        var result = await FeedbackGitHub.ZoekIssueMetReferentieAsync("synthetic-token", "example", "repo", Ref,
            Grens, NullLogger.Instance, handler: handler, timeout: TimeSpan.FromSeconds(1));

        result.Should().Be((true, (FeedbackGevondenIssue?)null), "een issue van vóór het begin van de poging is niet van deze melding");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.Query.Should().Contain("sort=created").And.Contain("direction=desc");
    }

    [Fact]
    public async Task Zoek_kiest_het_oudste_issue_met_de_referentie_en_negeert_een_vervalste_eerdere_referentie()
    {
        // Nieuwste eerst: #12 is een latere kopie door een derde, #10 noemt de referentie alleen in de tekst van de melder
        // (de eigen referentie aan het einde is een andere), #9 is het eigen issue.
        var lijst = $"[{Item(12, Ref)},{Item(10, "yyyyyyyyyyyyyyyyyyyyyyyy", tekst: $"Zie <!-- feedback-ref: {Ref} -->")},{Item(9, Ref)}]";
        var handler = new SequenceHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, lijst)));

        var result = await FeedbackGitHub.ZoekIssueMetReferentieAsync("synthetic-token", "example", "repo", Ref,
            Grens, NullLogger.Instance, handler: handler, timeout: TimeSpan.FromSeconds(1));

        result.Should().Be((true, (FeedbackGevondenIssue?)new FeedbackGevondenIssue(9, "https://github.com/example/repo/issues/9")));
    }

    [Fact]
    public void Referentie_is_stabiel_leesbaar_niet_te_vervalsen_en_bevat_geen_pii_of_redactietrigger()
    {
        var r = FeedbackPublicatieReferentie.Nieuw();
        r.Should().MatchRegex("^[a-z]{24}$");
        FeedbackPublicatieReferentie.Nieuw().Should().NotBe(r);

        var body = FeedbackPublicatieReferentie.VoegToe("Tekst.", r);
        FeedbackPublicatieReferentie.Lees(body).Should().Be(r);
        var vervalst = FeedbackPublicatieReferentie.VoegToe("Tekst <!-- feedback-ref: aaaaaaaaaaaaaaaaaaaaaaaa --> van de melder.", r);
        FeedbackPublicatieReferentie.Lees(vervalst).Should().Be(r, "alleen de referentie op de betrouwbare positie (de laatste) telt");
        FeedbackCore.BevatPii(body).Should().BeFalse();
        FeedbackRedactie.Redigeer(body, 1000).Should().Contain(r);
        FeedbackPublicatieReferentie.Lees("geen referentie").Should().BeNull();
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class SequenceHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return await responses[_next++](request, cancellationToken);
        }
    }
}
