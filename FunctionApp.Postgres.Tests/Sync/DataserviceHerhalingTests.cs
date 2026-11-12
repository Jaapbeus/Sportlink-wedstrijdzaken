using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Sync;
using FunctionApp.Postgres.Sync;
using System.Net;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

/// <summary>
/// #1779 (review P2/P3): alle vier dataservice-GET's van de Postgres-sync herhalen een tijdelijke
/// fout, en een brede storing blijft binnen het runbrede herhaalbudget. Geen database nodig: elk pad
/// eindigt vóór de eerste databaseaanroep (lege lijst of HTTP-fout).
/// </summary>
public class DataserviceHerhalingTests
{
    /// <summary>Wordt nooit gebruikt: elk pad eindigt vóór de eerste databaseaanroep.</summary>
    private const string NietGebruikteConnectie = "NIET-GEBRUIKT-IN-DEZE-PADEN";

    private sealed class ScriptHandler(params HttpStatusCode[] statussen) : HttpMessageHandler
    {
        public int Aanroepen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var status = statussen[Math.Min(Aanroepen, statussen.Length - 1)];
            Aanroepen++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("[]") });
        }
    }

    [Fact]
    public async Task Matchdetails_BredeStoring_BlijftBinnenHetRunbudget()
    {
        var handler = new ScriptHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var budget = new HerhaalBudget();
        const int codes = 20;

        for (var code = 0; code < codes; code++)
        {
            var ok = await PostgresSyncPipeline.FetchAndStoreMatchDetailsAsync(
                NietGebruikteConnectie,
                $"http://test/wedstrijd-informatie?wedstrijdcode={code}", "TEST", NullLogger.Instance,
                client, TimeSpan.Zero, budget);
            ok.Should().BeFalse();
        }

        handler.Aanroepen.Should().Be(codes + HerhaalBudget.StandaardPerRun,
            "zonder runbudget zou elke code drie aanroepen kosten (60)");
    }

    [Fact]
    public async Task Teams_TijdelijkeFout_WordtHerhaald()
    {
        var handler = new ScriptHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        await PostgresSyncPipeline.FetchAndStoreTeamsAsync(
            NietGebruikteConnectie, "http://test/teams", "TEST", NullLogger.Instance, new HttpClient(handler), TimeSpan.Zero);

        handler.Aanroepen.Should().Be(2);
    }

    [Fact]
    public async Task Programma_TijdelijkeFout_WordtHerhaald()
    {
        var handler = new ScriptHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        await PostgresSyncPipeline.FetchAndStoreProgrammaAsync(
            NietGebruikteConnectie, "http://test/programma", "TEST", NullLogger.Instance, new HttpClient(handler), TimeSpan.Zero);

        handler.Aanroepen.Should().Be(2);
    }

    [Fact]
    public async Task Uitslagen_TijdelijkeFout_WordtHerhaald()
    {
        var handler = new ScriptHandler(HttpStatusCode.BadGateway, HttpStatusCode.OK);

        await PostgresSyncPipeline.FetchAndStoreUitslagenAsync(
            NietGebruikteConnectie, "http://test/uitslagen", "TEST", NullLogger.Instance, new HttpClient(handler), TimeSpan.Zero);

        handler.Aanroepen.Should().Be(2);
    }

    [Fact]
    public async Task Uitslagen_BlijvendeFout_GooitZodatDeFaseMisluktTelt()
    {
        var handler = new ScriptHandler(HttpStatusCode.ServiceUnavailable);

        var act = async () => await PostgresSyncPipeline.FetchAndStoreUitslagenAsync(
            NietGebruikteConnectie, "http://test/uitslagen", "TEST", NullLogger.Instance, new HttpClient(handler), TimeSpan.Zero);

        await act.Should().ThrowAsync<HttpRequestException>("#1081 blijft: een blijvende fout laat de fase mislukken");
        handler.Aanroepen.Should().Be(SportlinkDataserviceHttp.MaxPogingen);
    }
}
