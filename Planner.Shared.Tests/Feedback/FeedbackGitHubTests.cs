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
