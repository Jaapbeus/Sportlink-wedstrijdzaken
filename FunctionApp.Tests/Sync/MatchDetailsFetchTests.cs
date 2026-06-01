using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using SportlinkFunction;
using System.Net;
using Xunit;

namespace FunctionApp.Tests.Sync;

/// <summary>
/// Tests voor FetchAndStoreMatchDetailsAsync — regressietest voor #464 (partialFailure).
/// Verifieert dat fouten correct als `false` worden gerapporteerd zodat
/// de caller partialFailure kan zetten.
/// </summary>
public class MatchDetailsFetchTests
{
    private static HttpClient MakeClient(HttpStatusCode statusCode)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode));
        return new HttpClient(handler.Object);
    }

    private static HttpClient MakeThrowingClient()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Netwerk onbeschikbaar"));
        return new HttpClient(handler.Object);
    }

    private static HttpClient MakeJsonErrorClient()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ INVALID JSON {{{{")
            });
        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task FetchAndStoreMatchDetails_HttpFout_RetourneertFalse()
    {
        var client = MakeThrowingClient();
        var log    = NullLogger.Instance;

        var result = await SportlinkSyncPipeline.FetchAndStoreMatchDetailsAsync(
            "http://test/wedstrijd-informatie?wedstrijdcode=1", log, client);

        result.Should().BeFalse("een HTTP-fout moet false teruggeven zodat de caller partialFailure zet (#464)");
    }

    [Fact]
    public async Task FetchAndStoreMatchDetails_Http500_RetourneertFalse()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        var log    = NullLogger.Instance;

        var result = await SportlinkSyncPipeline.FetchAndStoreMatchDetailsAsync(
            "http://test/wedstrijd-informatie?wedstrijdcode=2", log, client);

        result.Should().BeFalse("HTTP 500 moet false teruggeven (#464)");
    }

    [Fact]
    public async Task FetchAndStoreMatchDetails_OngeldigeJson_RetourneertFalse()
    {
        var client = MakeJsonErrorClient();
        var log    = NullLogger.Instance;

        var result = await SportlinkSyncPipeline.FetchAndStoreMatchDetailsAsync(
            "http://test/wedstrijd-informatie?wedstrijdcode=3", log, client);

        // JSON-deserialisatiefout geeft false (#464 — JSON-fouten tellen als failure)
        result.Should().BeFalse("een JSON-deserialisatiefout moet false teruggeven (#464)");
    }
}
