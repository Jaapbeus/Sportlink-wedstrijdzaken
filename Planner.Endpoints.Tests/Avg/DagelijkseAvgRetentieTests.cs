using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Avg;
using Planner.Shared.Avg;
using Planner.Shared.Feedback;
using Xunit;

namespace Planner.Endpoints.Tests.Avg;

/// <summary>
/// De dagelijkse AVG-retentierun (timer <c>CleanupFeedback</c>, #1698): de feedbackretentie en de
/// seizoensretentie van de historietabellen draaien in één timer, maar als onafhankelijke stappen.
/// Een fout in de ene stap mag de andere niet tegenhouden en moet daarna toch als mislukte run
/// zichtbaar worden voor de Functions-host.
/// </summary>
public class DagelijkseAvgRetentieTests
{
    private readonly List<string> _volgorde = [];

    [Fact]
    public async Task BeideStappenDraaien_NaHetWachtenOpDeDatabase()
    {
        await DagelijkseAvgRetentie.VoerUitAsync(
            Wacht, FeedbackStore(faalt: false), externeIntegratiesToegestaan: false, new HisOpslag(_volgorde, faalt: false),
            NullLogger.Instance);

        _volgorde.Should().Equal("wacht", "feedback", "his");
    }

    [Fact]
    public async Task FeedbackStapFaalt_HisStapDraaitToch_EnDeFoutWordtDoorgegooid()
    {
        var act = () => DagelijkseAvgRetentie.VoerUitAsync(
            Wacht, FeedbackStore(faalt: true), false, new HisOpslag(_volgorde, faalt: false), NullLogger.Instance);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("feedback kapot");
        _volgorde.Should().Contain("his", "een fout in de feedbackretentie mag de his-retentie niet tegenhouden");
    }

    [Fact]
    public async Task DatabaseWachtenFaalt_HisStapProbeertHetToch()
    {
        var act = () => DagelijkseAvgRetentie.VoerUitAsync(
            () => throw new TimeoutException("database start niet"), FeedbackStore(faalt: false), false,
            new HisOpslag(_volgorde, faalt: false), NullLogger.Instance);

        await act.Should().ThrowExactlyAsync<TimeoutException>();
        _volgorde.Should().Equal("his");
    }

    [Fact]
    public async Task HisStapFaalt_FeedbackIsGedraaid_EnDeFoutWordtDoorgegooid()
    {
        var act = () => DagelijkseAvgRetentie.VoerUitAsync(
            Wacht, FeedbackStore(faalt: false), false, new HisOpslag(_volgorde, faalt: true), NullLogger.Instance);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("his kapot");
        _volgorde.Should().Contain("feedback");
    }

    [Fact]
    public async Task BeideStappenFalen_AggregateExceptionMetBeideFouten()
    {
        var act = () => DagelijkseAvgRetentie.VoerUitAsync(
            Wacht, FeedbackStore(faalt: true), false, new HisOpslag(_volgorde, faalt: true), NullLogger.Instance);

        var fout = await act.Should().ThrowExactlyAsync<AggregateException>();
        fout.Which.InnerExceptions.Select(e => e.Message).Should().Equal("feedback kapot", "his kapot");
    }

    private Task Wacht()
    {
        _volgorde.Add("wacht");
        return Task.CompletedTask;
    }

    private IFeedbackStore FeedbackStore(bool faalt)
    {
        var store = new Mock<IFeedbackStore>(MockBehavior.Strict);
        var retentie = store.Setup(s => s.VoerRetentieUitAsync(It.IsAny<DateTime>()))
            .Callback(() => _volgorde.Add("feedback"));
        if (faalt)
            retentie.ThrowsAsync(new InvalidOperationException("feedback kapot"));
        else
            retentie.ReturnsAsync(new FeedbackRetentieResultaat(0, 0, 0));
        return store.Object;
    }

    private sealed class HisOpslag(List<string> volgorde, bool faalt) : IHisRetentieOpslag
    {
        public Task<IReadOnlyList<ClubSeizoenEinde>> LeesSeizoenseindenAsync()
        {
            volgorde.Add("his");
            return faalt
                ? throw new InvalidOperationException("his kapot")
                : Task.FromResult<IReadOnlyList<ClubSeizoenEinde>>([new ClubSeizoenEinde("ALLSTARS", new DateOnly(2000, 6, 30))]);
        }

        public Task<(int Matches, int MatchDetails)> WisTotEnMetAsync(string clubCode, DateOnly grens) => Task.FromResult((0, 0));
    }
}
