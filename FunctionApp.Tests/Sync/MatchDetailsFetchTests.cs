using AwesomeAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging;
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
            "http://test/wedstrijd-informatie?wedstrijdcode=1", "TEST", log, client);

        result.Should().BeFalse("een HTTP-fout moet false teruggeven zodat de caller partialFailure zet (#464)");
    }

    [Fact]
    public async Task FetchAndStoreMatchDetails_Http500_RetourneertFalse()
    {
        var client = MakeClient(HttpStatusCode.InternalServerError);
        var log    = NullLogger.Instance;

        var result = await SportlinkSyncPipeline.FetchAndStoreMatchDetailsAsync(
            "http://test/wedstrijd-informatie?wedstrijdcode=2", "TEST", log, client);

        result.Should().BeFalse("HTTP 500 moet false teruggeven (#464)");
    }

    [Fact]
    public async Task FetchAndStoreMatchDetails_OngeldigeJson_RetourneertFalse()
    {
        var client = MakeJsonErrorClient();
        var log    = NullLogger.Instance;

        var result = await SportlinkSyncPipeline.FetchAndStoreMatchDetailsAsync(
            "http://test/wedstrijd-informatie?wedstrijdcode=3", "TEST", log, client);

        // JSON-deserialisatiefout geeft false (#464 — JSON-fouten tellen als failure)
        result.Should().BeFalse("een JSON-deserialisatiefout moet false teruggeven (#464)");
    }

    // ── #1200 — de Sportlink-clientId mag nooit in een logregel belanden ────────────────────────
    //
    // Sportlink authenticeert op de dataservice via een queryparameter, dus de aanroep-URL ís een
    // secret. Het foutpad van deze methode logde de volledige URL; deze tests leggen de invariant
    // vast die daarbij ontbrak. De RecordingLogger vangt template, elke placeholder-waarde én de
    // exception-tekst op, zodat geen van die drie kanalen de waarde ongemerkt kan doorlaten.

    private const string SynthetischeClientId = "SYNTHETIC-CLIENT-ID-1234";

    private static string UrlMetClientId(int wedstrijdcode) =>
        $"http://test/wedstrijd-informatie?clientId={SynthetischeClientId}&wedstrijdcode={wedstrijdcode}";

    private static async Task BewijsDatClientIdNietGelogdWordtAsync(HttpClient client, int wedstrijdcode)
    {
        var logger = new RecordingLogger();

        var result = await SportlinkSyncPipeline.FetchAndStoreMatchDetailsAsync(
            UrlMetClientId(wedstrijdcode), "TEST", logger, client);

        result.Should().BeFalse("dit is een foutpad — anders bewijst de logassertie niets");
        logger.Levels.Should().Contain(LogLevel.Error,
            "zonder een gelogde foutregel zou deze test ook slagen als er helemaal niets gelogd wordt");
        logger.Messages.Should().NotContain(m => m.Contains(SynthetischeClientId),
            "de Sportlink-clientId staat als queryparameter in de aanroep-URL en mag nooit in een logregel belanden (#1200)");
        logger.Messages.Should().NotContain(m => m.Contains("clientId="),
            "ook de parameternaam met waarde mag nergens in een logregel staan (#1200)");
    }

    [Fact]
    public async Task FetchAndStoreMatchDetails_HttpFout_LogtClientIdNooit()
        => await BewijsDatClientIdNietGelogdWordtAsync(MakeThrowingClient(), 11);

    [Fact]
    public async Task FetchAndStoreMatchDetails_Http500_LogtClientIdNooit()
        => await BewijsDatClientIdNietGelogdWordtAsync(MakeClient(HttpStatusCode.InternalServerError), 12);

    [Fact]
    public async Task FetchAndStoreMatchDetails_OngeldigeJson_LogtClientIdNooit()
        => await BewijsDatClientIdNietGelogdWordtAsync(MakeJsonErrorClient(), 13);
}
