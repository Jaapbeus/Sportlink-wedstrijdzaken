using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using FunctionApp.Postgres.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

[CollectionDefinition("EmailGraphService", DisableParallelization = true)]
public sealed class EmailGraphServiceCollection
{
    public const string Name = "EmailGraphService";
}

[Collection(EmailGraphServiceCollection.Name)]
public class EmailGraphServiceTests
{
    [Fact]
    public async Task GetUnreadEmailsAsync_SuccesvolleLegeInbox_IsLegeLijst()
    {
        using var mailbox = new GraphMailboxEnvironment();
        using var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"value\":[]}"));
        using var httpClient = new HttpClient(handler);
        var service = new EmailGraphService(new GraphServiceClient(httpClient), NullLogger<EmailGraphService>.Instance);

        var result = await service.GetUnreadEmailsAsync();

        result.Should().BeEmpty();
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task GetUnreadEmailsAsync_BijGraphFout_GooitDoorInPlaatsVanLegeInbox()
    {
        using var mailbox = new GraphMailboxEnvironment();
        using var handler = new StubHandler(_ => Json(HttpStatusCode.ServiceUnavailable,
            "{\"error\":{\"code\":\"ServiceUnavailable\",\"message\":\"synthetic failure\"}}"));
        using var httpClient = new HttpClient(handler);
        var logger = new TypedRecordingLogger();
        var service = new EmailGraphService(new GraphServiceClient(httpClient), logger);

        var act = () => service.GetUnreadEmailsAsync();

        await act.Should().ThrowAsync<ApiException>();
        logger.Inner.Levels.Should().Contain(LogLevel.Error);
        logger.Inner.Messages.Should().Contain(message => message.Contains("Fout bij ophalen van ongelezen emails", StringComparison.Ordinal));
        logger.Inner.Messages.Should().NotContain(message => message.Contains("0 ongelezen email(s) opgehaald", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetCategoriesAsync_BijSucces_LogtPasNaDeGraphPatch()
    {
        using var mailbox = new GraphMailboxEnvironment();
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var httpClient = new HttpClient(handler);
        var logger = new TypedRecordingLogger();
        var service = new EmailGraphService(new GraphServiceClient(httpClient), logger);

        await service.SetCategoriesAsync("synthetic-message-id", "Geen AI antwoord");

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Patch);
        handler.Requests[0].Body.Should().Contain("\"categories\"").And.Contain("Geen AI antwoord");
        logger.Inner.Messages.Should().Contain(message => message.Contains("gemarkeerd met categorie(en)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetCategoriesAsync_BijGraphFout_GooitDoorEnLogtGeenSucces()
    {
        using var mailbox = new GraphMailboxEnvironment();
        using var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden,
            "{\"error\":{\"code\":\"Forbidden\",\"message\":\"synthetic failure\"}}"));
        using var httpClient = new HttpClient(handler);
        var logger = new TypedRecordingLogger();
        var service = new EmailGraphService(new GraphServiceClient(httpClient), logger);

        var act = () => service.SetCategoriesAsync("synthetic-message-id", "Geen AI antwoord");

        await act.Should().ThrowAsync<ApiException>();
        logger.Inner.Levels.Should().Contain(LogLevel.Error);
        logger.Inner.Messages.Should().Contain(message => message.Contains("Fout bij zetten van categorie", StringComparison.Ordinal));
        logger.Inner.Messages.Should().NotContain(message => message.Contains("gemarkeerd met categorie(en)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkAsReadAsync_BijGraphFout_GooitDoorEnLogtGeenSucces()
    {
        using var mailbox = new GraphMailboxEnvironment();
        using var handler = new StubHandler(_ => Json(HttpStatusCode.ServiceUnavailable,
            "{\"error\":{\"code\":\"ServiceUnavailable\",\"message\":\"synthetic failure\"}}"));
        using var httpClient = new HttpClient(handler);
        var logger = new TypedRecordingLogger();
        var service = new EmailGraphService(new GraphServiceClient(httpClient), logger);

        var act = () => service.MarkAsReadAsync("synthetic-message-id");

        await act.Should().ThrowAsync<ApiException>();
        logger.Inner.Levels.Should().Contain(LogLevel.Error);
        logger.Inner.Messages.Should().Contain(message => message.Contains("Fout bij markeren van email", StringComparison.Ordinal));
        logger.Inner.Messages.Should().NotContain(message => message.Contains("gemarkeerd als gelezen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkAsReadAsync_BijSucces_LogtNaDeGraphPatch()
    {
        using var mailbox = new GraphMailboxEnvironment();
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var httpClient = new HttpClient(handler);
        var logger = new TypedRecordingLogger();
        var service = new EmailGraphService(new GraphServiceClient(httpClient), logger);

        await service.MarkAsReadAsync("synthetic-message-id");

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Patch);
        handler.Requests[0].Body.Should().Contain("\"isRead\":true");
        logger.Inner.Messages.Should().Contain(message => message.Contains("gemarkeerd als gelezen", StringComparison.Ordinal));
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
        => new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class GraphMailboxEnvironment : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("GraphMailbox");

        public GraphMailboxEnvironment() => Environment.SetEnvironmentVariable("GraphMailbox", "test-mailbox@allstars-fc.test");

        public void Dispose() => Environment.SetEnvironmentVariable("GraphMailbox", _previous);
    }

    private sealed class TypedRecordingLogger : ILogger<EmailGraphService>
    {
        public RecordingLogger Inner { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri?.ToString() ?? "", body));
            return response(request);
        }
    }
}
