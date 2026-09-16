using AwesomeAssertions;
using FunctionApp.Postgres.Sync;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging;
using System.Net;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

/// <summary>
/// Postgres-tier-tegenhanger van de #1200-logregressietests in
/// <c>FunctionApp.Tests/Sync/MatchDetailsFetchTests.cs</c>.
/// <para>
/// Sportlink authenticeert op de dataservice via een <b>queryparameter</b>, dus de aanroep-URL ís
/// een secret. Het foutpad van <c>FetchAndStoreMatchDetailsAsync</c> logde die volledige URL; deze
/// tests leggen de invariant vast die daarbij ontbrak: de clientId belandt nooit in een logregel —
/// niet via de template, niet via een placeholder-waarde, en niet via de exception-tekst.
/// </para>
/// <para>
/// Geen Postgres nodig: alle drie de paden falen vóór de eerste databaseaanroep, dus deze tests
/// draaien ook zonder de integratie-omgeving van de overige suites in dit project.
/// </para>
/// </summary>
public class MatchDetailsFetchLoggingTests
{
    private const string SynthetischeClientId = "SYNTHETIC-CLIENT-ID-1234";

    /// <summary>
    /// Wordt nooit gebruikt: elk pad hieronder faalt op de HTTP-aanroep of op de JSON-parse, dus
    /// vóór de eerste databaseaanroep. Een echte connectiestring hoort sowieso nooit in een test.
    /// </summary>
    private const string NietGebruikteConnectie = "NIET-GEBRUIKT-IN-DEZE-FOUTPADEN";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _antwoord;
        public StubHandler(Func<HttpResponseMessage> antwoord) => _antwoord = antwoord;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_antwoord());
    }

    private static HttpClient GooiendeClient() => new(new StubHandler(
        () => throw new HttpRequestException("Netwerk onbeschikbaar")));

    private static HttpClient StatusClient(HttpStatusCode status) => new(new StubHandler(
        () => new HttpResponseMessage(status)));

    private static HttpClient OngeldigeJsonClient() => new(new StubHandler(
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ INVALID JSON {{{{") }));

    private static string UrlMetClientId(int wedstrijdcode) =>
        $"http://test/wedstrijd-informatie?clientId={SynthetischeClientId}&wedstrijdcode={wedstrijdcode}";

    private static async Task BewijsDatClientIdNietGelogdWordtAsync(HttpClient client, int wedstrijdcode)
    {
        var logger = new RecordingLogger();

        var result = await PostgresSyncPipeline.FetchAndStoreMatchDetailsAsync(
            NietGebruikteConnectie, UrlMetClientId(wedstrijdcode), "TEST", logger, client);

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
        => await BewijsDatClientIdNietGelogdWordtAsync(GooiendeClient(), 11);

    [Fact]
    public async Task FetchAndStoreMatchDetails_Http500_LogtClientIdNooit()
        => await BewijsDatClientIdNietGelogdWordtAsync(StatusClient(HttpStatusCode.InternalServerError), 12);

    [Fact]
    public async Task FetchAndStoreMatchDetails_OngeldigeJson_LogtClientIdNooit()
        => await BewijsDatClientIdNietGelogdWordtAsync(OngeldigeJsonClient(), 13);
}
