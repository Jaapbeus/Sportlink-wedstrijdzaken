using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>Begrensde herhaalpogingen voor dataservice-GET's (#1779).</summary>
public class SportlinkDataserviceHttpTests
{
    private sealed class ScriptHandler(params Func<HttpResponseMessage>[] antwoorden) : HttpMessageHandler
    {
        public int Aanroepen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Math.Min(Aanroepen, antwoorden.Length - 1);
            Aanroepen++;
            return Task.FromResult(antwoorden[index]());
        }
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode s) => () => new HttpResponseMessage(s);

    private static async Task<(HttpResponseMessage? response, ScriptHandler handler, List<string> meldingen)> Run(
        params Func<HttpResponseMessage>[] antwoorden)
    {
        var handler = new ScriptHandler(antwoorden);
        var meldingen = new List<string>();
        using var client = new HttpClient(handler);
        var response = await SportlinkDataserviceHttp.GetMetHerhalingAsync(
            client, "http://test/x", new HerhaalBudget(), meldingen.Add, TimeSpan.Zero);
        return (response, handler, meldingen);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task TijdelijkeStatus_WordtHerhaaldTotSucces(HttpStatusCode tijdelijk)
    {
        var (response, handler, meldingen) = await Run(Status(tijdelijk), Status(HttpStatusCode.OK));

        response!.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Aanroepen.Should().Be(2);
        meldingen.Should().HaveCount(1);
    }

    [Fact]
    public async Task BlijvendeFout_StopptNaMaxPogingenEnGeeftLaatsteResponsTerug()
    {
        var (response, handler, _) = await Run(Status(HttpStatusCode.InternalServerError));

        handler.Aanroepen.Should().Be(SportlinkDataserviceHttp.MaxPogingen);
        response!.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "de aanroeper beslist met EnsureSuccessStatusCode (#1081 blijft)");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task PermanenteClientfout_WordtNooitHerhaald(HttpStatusCode status)
    {
        var (_, handler, meldingen) = await Run(Status(status));

        handler.Aanroepen.Should().Be(1);
        meldingen.Should().BeEmpty();
    }

    [Fact]
    public async Task NetwerkfoutDaarnaSucces_WordtHerhaald()
    {
        var handler = new ScriptHandler(
            () => throw new HttpRequestException("onbereikbaar"),
            Status(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var response = await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", new HerhaalBudget(), null, TimeSpan.Zero);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Aanroepen.Should().Be(2);
    }

    [Fact]
    public async Task NetwerkfoutBijLaatstePoging_WordtDoorgegooid()
    {
        var handler = new ScriptHandler(() => throw new HttpRequestException("onbereikbaar"));
        using var client = new HttpClient(handler);

        var act = async () => await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", new HerhaalBudget(), null, TimeSpan.Zero);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Aanroepen.Should().Be(SportlinkDataserviceHttp.MaxPogingen);
    }

    [Fact]
    public async Task TimeOut_WordtHerhaald()
    {
        var handler = new ScriptHandler(
            () => throw new TaskCanceledException("time-out"),
            Status(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var response = await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", new HerhaalBudget(), null, TimeSpan.Zero);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnnuleringDoorAanroeper_WordtNietHerhaald()
    {
        var handler = new ScriptHandler(() => throw new TaskCanceledException("geannuleerd"));
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", new HerhaalBudget(), null, TimeSpan.Zero, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Aanroepen.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Melding_BevatNooitDeUrl()
    {
        var (_, _, meldingen) = await Run(Status(HttpStatusCode.InternalServerError), Status(HttpStatusCode.OK));

        meldingen.Should().NotContain(m => m.Contains("http://") || m.Contains("clientId"));
    }

    // ── Runbreed herhaalbudget (#1779, review P2) ─────────────────────────────────────────────
    [Fact]
    public async Task BredeStoring_TotaalAantalAanroepenBlijftBinnenHetRunbudget()
    {
        // 50 wedstrijdcodes, de dataservice geeft overal 503: zonder runbudget 3 × 50 = 150 aanroepen
        // (en 100 × 2 s wachten). Met het budget hoogstens 50 + StandaardPerRun.
        var handler = new ScriptHandler(Status(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var budget = new HerhaalBudget();
        const int codes = 50;

        for (var i = 0; i < codes; i++)
        {
            using var response = await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", budget, null, TimeSpan.Zero);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        handler.Aanroepen.Should().Be(codes + HerhaalBudget.StandaardPerRun);
        budget.Resterend.Should().Be(0);
    }

    [Fact]
    public async Task BudgetOp_DoetEenPogingEnMeldtDat()
    {
        var handler = new ScriptHandler(Status(HttpStatusCode.InternalServerError), Status(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var meldingen = new List<string>();

        using var response = await SportlinkDataserviceHttp.GetMetHerhalingAsync(
            client, "http://test/x", new HerhaalBudget(0), meldingen.Add, TimeSpan.Zero);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        handler.Aanroepen.Should().Be(1);
        meldingen.Should().ContainSingle().Which.Should().Contain("herhaalbudget");
    }

    [Fact]
    public async Task NetwerkfoutMetLeegBudget_WordtMeteenDoorgegooid()
    {
        var handler = new ScriptHandler(() => throw new HttpRequestException("onbereikbaar"));
        using var client = new HttpClient(handler);

        var act = async () => await SportlinkDataserviceHttp.GetMetHerhalingAsync(client, "http://test/x", new HerhaalBudget(0), null, TimeSpan.Zero);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Aanroepen.Should().Be(1);
    }

    [Fact]
    public void HerhaalBudget_NegatieveWaardeIsLeeg()
    {
        var budget = new HerhaalBudget(-3);
        budget.Resterend.Should().Be(0);
        budget.Neem().Should().BeFalse();
    }

    // ── HaalTekstAsync: gedeelde ophaalroute van alle dataservice-fetchers (#1779) ─────────────
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Berichten { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Berichten.Add(formatter(state, exception));
    }

    private static Func<HttpResponseMessage> Tekst(string body)
        => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    [Fact]
    public async Task HaalTekst_HerhaaltTijdelijkeFoutEnGeeftDeInhoud_ZonderUrlInLog()
    {
        var handler = new ScriptHandler(Status(HttpStatusCode.BadGateway), Tekst("[]"));
        using var client = new HttpClient(handler);
        var log = new RecordingLogger();

        var tekst = await SportlinkDataserviceHttp.HaalTekstAsync(
            client, "http://test/teams?clientId=geheim", new HerhaalBudget(), "TEAMS", log, TimeSpan.Zero);

        tekst.Should().Be("[]");
        handler.Aanroepen.Should().Be(2);
        log.Berichten.Should().ContainSingle().Which.Should().Contain("TEAMS").And.Contain("502")
            .And.NotContain("geheim").And.NotContain("http://");
    }

    [Fact]
    public async Task HaalTekst_BlijvendeFout_Gooit()
    {
        var handler = new ScriptHandler(Status(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);

        var act = async () => await SportlinkDataserviceHttp.HaalTekstAsync(
            client, "http://test/x", new HerhaalBudget(), "TEAMS", new RecordingLogger(), TimeSpan.Zero);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Aanroepen.Should().Be(SportlinkDataserviceHttp.MaxPogingen);
    }
}
