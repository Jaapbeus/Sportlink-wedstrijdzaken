using System.Text.Json;
using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

/// <summary>
/// Tijdsgrens van de herhaalpogingen (#1779, review P2): een dataservice die hangt in plaats van
/// een fout te geven, mag de run niet via herhaalde time-outs over de <c>functionTimeout</c> duwen.
/// Alles draait op een nepklok; de handler bootst een hangende server na die pas stopt bij
/// annulering of bij de time-out van de <see cref="HttpClient"/>.
/// </summary>
public class SportlinkDataserviceHttpLooptijdTests
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(100);

    /// <summary>Handmatige klok met timers die alleen bij <see cref="Verzet"/> afgaan.</summary>
    private sealed class NepKlok : TimeProvider
    {
        private readonly List<NepTimer> _timers = new();
        public DateTimeOffset Nu { get; private set; } = new(2026, 11, 11, 3, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Nu;
        public override long GetTimestamp() => Nu.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new NepTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Verzet(TimeSpan duur)
        {
            Nu += duur;
            foreach (var timer in _timers.ToList())
                timer.GaAfAlsVerlopen(Nu);
        }

        private sealed class NepTimer(NepKlok klok, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _moment;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _moment = dueTime == Timeout.InfiniteTimeSpan ? null : klok.Nu + dueTime;
                return true;
            }

            public void GaAfAlsVerlopen(DateTimeOffset nu)
            {
                if (_moment is not { } moment || moment > nu)
                    return;
                _moment = null;
                callback(state);
            }

            public void Dispose() => _moment = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>
    /// Hangende dataservice: verzet de klok per seconde tot de aanroep wordt afgebroken, of tot
    /// de time-out van de HttpClient (100 s) verstrijkt. Legt per poging de duur vast.
    /// </summary>
    private sealed class HangendeHandler(NepKlok klok) : HttpMessageHandler
    {
        public List<TimeSpan> Duren { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var start = klok.Nu;
            try
            {
                while (klok.Nu - start < ClientTimeout)
                {
                    ct.ThrowIfCancellationRequested();
                    klok.Verzet(TimeSpan.FromSeconds(1));
                }
                ct.ThrowIfCancellationRequested();
                throw new TaskCanceledException("time-out van de HttpClient");
            }
            finally
            {
                Duren.Add(klok.Nu - start);
            }
        }
    }

    private static async Task<List<string>> HaalOpTotFout(HttpClient client, HerhaalBudget budget, int aanroepen)
    {
        var meldingen = new List<string>();
        for (var i = 0; i < aanroepen; i++)
        {
            var act = async () => await SportlinkDataserviceHttp.GetMetHerhalingAsync(
                client, "http://test/x", budget, meldingen.Add, TimeSpan.Zero);
            await act.Should().ThrowAsync<TaskCanceledException>();
        }
        return meldingen;
    }

    [Fact]
    public async Task HangendeDataservice_HerhaaldePogingDuurtHoogstensDePogingLimiet()
    {
        var klok = new NepKlok();
        var handler = new HangendeHandler(klok);
        using var client = new HttpClient(handler);
        var budget = new HerhaalBudget(looptijd: TimeSpan.FromHours(1), pogingLimiet: TimeSpan.FromSeconds(30), klok: klok);
        var start = klok.Nu;

        await HaalOpTotFout(client, budget, 1);

        handler.Duren.Should().Equal(ClientTimeout, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        (klok.Nu - start).Should().Be(TimeSpan.FromSeconds(160), "zonder poginglimiet zou dit 3 × 100 s zijn");
    }

    [Fact]
    public async Task HangendeDataservice_HerhalingenStoppenOpDeRunbredeDeadline()
    {
        // Deadline 250 s, geen poginglimiet die eerder ingrijpt, ruim budget: alleen de deadline
        // begrenst. Code 1: 100 s + 100 s + afgebroken op 250 s; daarna geen herhaling meer.
        var klok = new NepKlok();
        var handler = new HangendeHandler(klok);
        using var client = new HttpClient(handler);
        var budget = new HerhaalBudget(extraPogingen: 100, looptijd: TimeSpan.FromSeconds(250),
            pogingLimiet: TimeSpan.FromHours(1), klok: klok);
        var start = klok.Nu;

        var meldingen = await HaalOpTotFout(client, budget, 5);

        handler.Duren.Should().HaveCount(3 + 4, "na de deadline doet elke aanroep weer één poging");
        handler.Duren.Take(3).Should().Equal(ClientTimeout, ClientTimeout, TimeSpan.FromSeconds(50));
        (klok.Nu - start).Should().Be(TimeSpan.FromSeconds(250 + 4 * 100),
            "zonder deadline kost een hangende dataservice 5 × 3 × 100 s");
        meldingen.Should().Contain(m => m.Contains("looptijd"));
        budget.Resterend.Should().Be(98);
    }

    [Fact]
    public async Task AnnuleringDoorAanroeper_TijdensHerhaaldePoging_WordtNietHerhaald()
    {
        var klok = new NepKlok();
        var handler = new HangendeHandler(klok);
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(110), klok);
        var budget = new HerhaalBudget(klok: klok);

        var act = async () => await SportlinkDataserviceHttp.GetMetHerhalingAsync(
            client, "http://test/x", budget, null, TimeSpan.Zero, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Duren.Should().HaveCount(2, "de annulering van de aanroeper valt in de tweede poging");
        budget.Resterend.Should().Be(HerhaalBudget.StandaardPerRun - 1);
    }

    [Theory]
    [InlineData("FunctionApp/host.json")]
    [InlineData("FunctionApp.Postgres/host.json")]
    public void StandaardGrenzen_LiggenRuimOnderDeFunctionTimeout(string hostJson)
    {
        var timeout = LeesFunctionTimeout(hostJson);
        var hoogstensDoorHerhalingen = HerhaalBudget.StandaardPerRun
            * (SportlinkDataserviceHttp.StandaardWachttijd + HerhaalBudget.StandaardPogingLimiet);

        (timeout - HerhaalBudget.StandaardLooptijd).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5),
            "na de laatste herhaling moeten merge, reconciliatie en foutrapportage (#1081) nog kunnen draaien");
        hoogstensDoorHerhalingen.Should().BeLessThan(timeout / 2);
    }

    private static TimeSpan LeesFunctionTimeout(string relatiefPad)
    {
        var map = new DirectoryInfo(AppContext.BaseDirectory);
        while (map is not null && !File.Exists(Path.Combine(map.FullName, relatiefPad)))
            map = map.Parent;
        map.Should().NotBeNull($"{relatiefPad} hoort in de repository te staan");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(map!.FullName, relatiefPad)));
        return TimeSpan.Parse(doc.RootElement.GetProperty("functionTimeout").GetString()!);
    }
}
