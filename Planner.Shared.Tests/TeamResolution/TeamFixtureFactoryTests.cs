using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Planner.Shared.TeamResolution;
using Xunit;

namespace Planner.Shared.Tests.TeamResolution;

public sealed class TeamFixtureFactoryTests
{
    [Theory]
    [InlineData(null, null, "ALLSTARS", EigenTeamBesluit.Onbekend)]
    [InlineData("JO13-1", "MO13-1", "", EigenTeamBesluit.Onbekend)]
    [InlineData("unknown", "other", "ALLSTARS", EigenTeamBesluit.Onbekend)]
    [InlineData("JO13-1", "unknown", "ALLSTARS", EigenTeamBesluit.Herkend)]
    [InlineData("unknown", "MO13-1", "ALLSTARS", EigenTeamBesluit.Herkend)]
    [InlineData("JO13-1", "same", "ALLSTARS", EigenTeamBesluit.Herkend)]
    [InlineData("13-1", "MO13-1", "ALLSTARS", EigenTeamBesluit.Handmatig)]
    [InlineData("JO13-1", "failure", "ALLSTARS", EigenTeamBesluit.Handmatig)]
    public async Task NoFixtureDecision_DoesNotInvokeFactory(
        string? team, string? opponent, string club, EigenTeamBesluit expected)
    {
        var creations = 0;

        var result = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            team, opponent, new Resolver(), club, NullLogger.Instance, null,
            () => { creations++; throw new InvalidOperationException("Must not construct"); }, "2026-11-10");

        Assert.Equal(expected, result.Besluit);
        Assert.Equal(0, creations);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("2026-02-30", null, null)]
    [InlineData("2026-11-10", "25:30", null)]
    [InlineData("2026-11-10", null, "2026-11-11")]
    public async Task InvalidContext_DoesNotInvokeFactory(string? date, string? time, string? otherDate)
    {
        var creations = 0;

        var result = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync(
            "JO13-1", "MO13-1", new Resolver(), "ALLSTARS", NullLogger.Instance, null,
            () => { creations++; throw new InvalidOperationException("Must not construct"); },
            date, time, otherDate is null ? null : [otherDate]);

        Assert.Equal(EigenTeamBesluit.Handmatig, result.Besluit);
        Assert.Equal(0, creations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructionOrQueryFailure_LeavesClassificationForManualReview(bool queryFailure)
    {
        var creations = 0;
        var repository = new RecordingFixtures { Fail = queryFailure };
        var classification = new BerichtClassificatie
        {
            TeamNaam = "JO13-1", Tegenstander = "MO13-1", Datum = "2026-11-10"
        };

        var recognized = await EigenTeamBepaling.BereidPlannerVoorAsync(
            classification, new Resolver(), "ALLSTARS", NullLogger.Instance, null,
            () =>
            {
                creations++;
                return queryFailure ? repository : throw new InvalidOperationException("Unavailable");
            });

        Assert.False(recognized);
        Assert.True(classification.TeamHandmatigBeoordelen);
        Assert.Equal("JO13-1", classification.TeamNaam);
        Assert.Equal("MO13-1", classification.Tegenstander);
        Assert.Equal(1, creations);
        Assert.Equal(queryFailure ? 1 : 0, repository.Calls);
    }

    [Fact]
    public async Task ValidConflict_CreatesOnceAndQueriesExactClubDateAndResolvedIds()
    {
        var creations = 0;
        var repository = new RecordingFixtures();
        var classification = new BerichtClassificatie
        {
            TeamNaam = "JO13-1", Tegenstander = "MO13-1", Datum = "2026-11-10", AanvangsTijd = "10:30"
        };

        var recognized = await EigenTeamBepaling.BereidPlannerVoorAsync(
            classification, new Resolver(), "ALLSTARS", NullLogger.Instance, null,
            () => { creations++; return repository; });

        Assert.True(recognized);
        Assert.False(classification.TeamHandmatigBeoordelen);
        Assert.False(classification.TeamNaamOnbevestigd);
        Assert.Equal("MO13-1", classification.TeamNaam);
        Assert.Equal("JO13-1", classification.Tegenstander);
        Assert.Equal(1, creations);
        Assert.Equal(1, repository.Calls);
        Assert.Equal("ALLSTARS", repository.Club);
        Assert.Equal(new DateOnly(2026, 11, 10), repository.Date);
        Assert.Equal([7, 11], repository.CandidateIds);
    }

    private sealed class RecordingFixtures : ITeamFixtureContextRepository
    {
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public string? Club { get; private set; }
        public DateOnly Date { get; private set; }
        public IReadOnlyList<int>? CandidateIds { get; private set; }

        public Task<TeamFixtureContext> GetAsync(string clubCode, DateOnly date, IReadOnlyList<int> candidateIds)
        {
            Calls++;
            Club = clubCode;
            Date = date;
            CandidateIds = candidateIds.ToArray();
            return Fail ? Task.FromException<TeamFixtureContext>(new InvalidOperationException("Unavailable"))
                : Task.FromResult(new TeamFixtureContext(
                    [new("fixture", "MO13-1", "JO13-1", "MO13-1", "10:30")],
                    [new(7, "JO13-1"), new(11, "MO13-1")]));
        }
    }

    private sealed class Resolver : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
            => request.RuweTeamTekst switch
            {
                "failure" => throw new InvalidOperationException("Unavailable"),
                "13-1" => Task.FromResult(new TeamResolutionResult(null, null, 0,
                    [new(7, "JO13-1", "JO13"), new(11, "MO13-1", "MO13")], ResolutionBron.MeerdereKandidaten)),
                "JO13-1" or "same" => Task.FromResult(new TeamResolutionResult(7, "JO13-1", 1, [], ResolutionBron.ExacteMatch)),
                "MO13-1" => Task.FromResult(new TeamResolutionResult(11, "MO13-1", 1, [], ResolutionBron.ExacteMatch)),
                _ => Task.FromResult(TeamResolutionResult.Onopgelost)
            };
    }
}
