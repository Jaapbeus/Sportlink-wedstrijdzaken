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

    /// <summary>Elke aanroep geeft een vast aantal wedstrijden terug en onthoudt de opgevraagde query's.</summary>
    private sealed class PlafondHandler(int aantal) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.Query);
            var rijen = string.Join(",", Enumerable.Range(1, aantal).Select(i => "{\"wedstrijdcode\":" + i + "}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + rijen + "]") });
        }
    }

    [Fact]
    public async Task Programma_VensterOnderHetPlafond_IsVolledigMetEenAanroep()
    {
        var handler = new PlafondHandler(0);

        var volledig = await PostgresSyncPipeline.FetchAndStoreProgrammaAsync(
            NietGebruikteConnectie, "http://test/programma?weekoffset=0", "TEST", NullLogger.Instance, new HttpClient(handler), TimeSpan.Zero);

        volledig.Should().BeTrue();
        handler.Urls.Should().ContainSingle();
    }

    [Fact]
    public async Task Programma_VensterOpHetPlafond_WordtGesplitstInThuisEnUit()
    {
        // #1915: 100 rijen = afgekapt. De opslag volgt pas na de ophaling en bestaat in deze test niet; de drie
        // aanroepen gebeuren daarvoor en bewijzen de bedrading.
        var handler = new PlafondHandler(100);

        var act = async () => await PostgresSyncPipeline.FetchAndStoreProgrammaAsync(
            NietGebruikteConnectie, "http://test/programma?weekoffset=0", "TEST", NullLogger.Instance, new HttpClient(handler), TimeSpan.Zero);

        await act.Should().ThrowAsync<Exception>();
        handler.Urls.Should().HaveCount(3);
        handler.Urls[1].Should().Contain("thuis=JA&uit=NEE");
        handler.Urls[2].Should().Contain("thuis=NEE&uit=JA");
    }
}
