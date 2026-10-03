using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Feedback;
using Xunit;

namespace Planner.Shared.Tests.Feedback;

/// <summary>Bewaartermijn-run (#764): eerst de issuestatus synchroniseren, dan pas wissen; nooit op een gok.</summary>
public class FeedbackRetentieCoreTests
{
    private sealed class Store : IFeedbackStore
    {
        public List<string> Volgorde { get; } = [];
        public List<FeedbackIssueVerwijzing> TeControleren { get; set; } = [];
        public Dictionary<Guid, DateTime?> Statussen { get; } = [];

        public Task<IReadOnlyList<FeedbackIssueVerwijzing>> TeControlerenIssuesAsync(int max)
        {
            Volgorde.Add("lees-issues");
            return Task.FromResult<IReadOnlyList<FeedbackIssueVerwijzing>>(TeControleren);
        }

        public Task ZetIssueStatusAsync(Guid feedbackId, DateTime? geslotenOpUtc)
        {
            Volgorde.Add("zet-status");
            Statussen[feedbackId] = geslotenOpUtc;
            return Task.CompletedTask;
        }

        public Task<FeedbackRetentieResultaat> VoerRetentieUitAsync(DateTime nuUtc)
        {
            Volgorde.Add("retentie");
            return Task.FromResult(new FeedbackRetentieResultaat(2, 3, 4));
        }

        public Task<int> TelRecenteMeldingenAsync(string c, string? m, DateTime s) => throw new NotSupportedException();
        public Task BewaarAsync(FeedbackNieuw n, IReadOnlyList<FeedbackTelemetrieRegel> t) => throw new NotSupportedException();
        public Task<bool> ClaimPublicatieAsync(string c, Guid f) => throw new NotSupportedException();
        public Task ZetGepubliceerdAsync(string c, Guid f, int n, string u) => throw new NotSupportedException();
        public Task ZetPublicatieMisluktAsync(string c, Guid f) => throw new NotSupportedException();
        public Task<FeedbackLijstResultaat> LijstAsync(string c, FeedbackFilter f) => throw new NotSupportedException();
        public Task<FeedbackDetail?> GetDetailAsync(string c, Guid f) => throw new NotSupportedException();
        public Task LogInzageAsync(FeedbackInzageNieuw i) => throw new NotSupportedException();
        public Task<FeedbackInzageLijst> LijstInzageAsync(string c, int l, int o) => throw new NotSupportedException();
    }

    [Fact]
    public void Termijnen_ZijnDeEigenaarsbesluiten()
    {
        FeedbackRetentie.IdentiteitNaSluitingMaanden.Should().Be(24);
        FeedbackRetentie.TelemetrieDagen.Should().Be(90);
        FeedbackRetentie.InzageLogMaanden.Should().Be(24);
    }

    [Fact]
    public async Task SynchroniseertEerstDeIssuestatus_DanPasDeRetentie()
    {
        var id = Guid.NewGuid();
        var store = new Store { TeControleren = [new FeedbackIssueVerwijzing(id, 41)] };
        var gesloten = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var resultaat = await FeedbackRetentieCore.VoerUitAsync(store, _ => Task.FromResult((true, (DateTime?)gesloten)), NullLogger.Instance);

        store.Volgorde.Should().Equal("lees-issues", "zet-status", "retentie");
        store.Statussen[id].Should().Be(gesloten);
        resultaat.Should().Be(new FeedbackRetentieResultaat(2, 3, 4));
    }

    [Fact]
    public async Task GitHubStatusOnbekend_LegtGeenStatusVastMaarDraaitDeRetentieWel()
    {
        var id = Guid.NewGuid();
        var store = new Store { TeControleren = [new FeedbackIssueVerwijzing(id, 41)] };

        await FeedbackRetentieCore.VoerUitAsync(store, _ => Task.FromResult((false, (DateTime?)null)), NullLogger.Instance);

        store.Statussen.Should().BeEmpty("een mislukte controle mag de bewaartermijn nooit laten verspringen");
        store.Volgorde.Should().Contain("retentie");
    }

    [Fact]
    public async Task HeropendIssue_WisHetSluitmoment()
    {
        var id = Guid.NewGuid();
        var store = new Store { TeControleren = [new FeedbackIssueVerwijzing(id, 41)] };

        await FeedbackRetentieCore.VoerUitAsync(store, _ => Task.FromResult((true, (DateTime?)null)), NullLogger.Instance);

        store.Statussen[id].Should().BeNull();
    }

    [Fact]
    public async Task EenOnbereikbaarIssue_HoudtDeRestEnDeRetentieNietTegen()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var store = new Store { TeControleren = [new FeedbackIssueVerwijzing(a, 1), new FeedbackIssueVerwijzing(b, 2)] };

        await FeedbackRetentieCore.VoerUitAsync(store, nr => nr == 1
            ? throw new HttpRequestException("timeout")
            : Task.FromResult((true, (DateTime?)null)), NullLogger.Instance);

        store.Statussen.Keys.Should().BeEquivalentTo(new[] { b });
        store.Volgorde.Should().Contain("retentie");
    }

    [Fact]
    public async Task ZonderGitHub_WordtDeStatussynchronisatieOvergeslagen()
    {
        var store = new Store { TeControleren = [new FeedbackIssueVerwijzing(Guid.NewGuid(), 1)] };

        await FeedbackRetentieCore.VoerUitAsync(store, null, NullLogger.Instance);

        store.Volgorde.Should().Equal("retentie");
    }
}
