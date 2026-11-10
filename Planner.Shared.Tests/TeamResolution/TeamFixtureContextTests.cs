using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.TeamResolution;
using Xunit;
using Planner.Shared.Email;
using Planner.Shared.Tests.Email.TestDoubles;

namespace Planner.Shared.Tests.TeamResolution;

public sealed class TeamFixtureContextTests
{
    private static readonly TeamFixtureSpelling[] Names = [new(1, "JO13-1"), new(2, "MO13-1")];
    private static TeamFixture Fixture(string owner = "JO13-1", string key = "fixture") => new(key, owner, "JO13-1", "MO13-1", "10:30");

    [Theory]
    [InlineData("JO13-1", 1)]
    [InlineData("MO13-1", 2)]
    public void UniqueSourceOwnership_SelectsOwnerNotFirstParticipant(string owner, int expected)
        => Assert.Equal(expected, TeamFixtureChoice.Choose(new([Fixture(owner)], Names), 1, 2, null));

    [Fact]
    public void MirroredOwnership_RemainsManualEvenForOnePhysicalFixture()
        => Assert.Null(TeamFixtureChoice.Choose(new([Fixture(), Fixture("MO13-1")], Names), 1, 2, null));

    [Fact]
    public void ParticipantMembershipWithoutSourceOwnership_DoesNotChoose()
        => Assert.Null(TeamFixtureChoice.Choose(new([Fixture("unconfirmed")], Names), 1, 2, null));

    [Fact]
    public void AmbiguousOwnershipCannotBeIgnoredToManufactureUniqueness()
    {
        TeamFixtureSpelling[] spellings = [.. Names, new(1, "ambiguous"), new(2, "ambiguous")];
        Assert.Null(TeamFixtureChoice.Choose(new([Fixture(), Fixture("ambiguous", "uncertain")], spellings), 1, 2, null));
    }

    [Fact]
    public void MultipleFixtureContexts_DoNotChoose()
        => Assert.Null(TeamFixtureChoice.Choose(new([Fixture(), Fixture(key: "another")], Names), 1, 2, null));

    [Fact]
    public void SameSourceKeyWithConflictingTimes_IsManual()
        => Assert.Null(TeamFixtureChoice.Choose(new([Fixture(), Fixture() with { Time = "09:00" }], Names), 1, 2, "10:30"));

    [Fact]
    public void AnotherFixtureForSelectedOwner_CannotBeHiddenByOpponentOrTimeFilter()
    {
        var other = Fixture(key: "earlier") with { Away = "external", Time = "09:00" };
        Assert.Null(TeamFixtureChoice.Choose(new([Fixture(), other], Names), 1, 2, "10:30"));
    }

    [Fact]
    public void UnmatchedOpponent_DoesNotChoose()
        => Assert.Null(TeamFixtureChoice.Choose(new([Fixture() with { Away = "unconfirmed" }], Names), 1, 2, null));

    [Theory]
    [InlineData(null, null)]
    [InlineData("2026-02-30", null)]
    [InlineData("2026-11-10", "25:30")]
    public async Task MissingOrInvalidContext_StopsBeforeQuery(string? date, string? time)
    {
        var repository = new Fixtures { Throw = true };
        var result = await Determine(repository, date, time);
        Assert.Equal(EigenTeamBesluit.Handmatig, result.Besluit);
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoEvidenceOrQueryFailure_IsTypedManual(bool failure)
    {
        var result = await Determine(new Fixtures { Throw = failure }, "2026-11-10", null);
        Assert.Equal(EigenTeamBesluit.Handmatig, result.Besluit);
        Assert.False(result.Herkend);
    }

    [Fact]
    public async Task PositiveSourceOwnership_CanonicalizesAndSwapsWhenSecondOwnTeamWins()
    {
        var result = await Determine(new Fixtures { Rows = [Fixture("MO13-1")] }, "2026-11-10", null);
        Assert.Equal(new EigenTeamUitkomst(true, "MO13-1", "JO13-1"), result);
    }

    [Fact]
    public async Task UnresolvedMultipleCandidates_NeverConsultFixtures()
    {
        var repository = new Fixtures { Throw = true };
        var result = await EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync("13-1", "MO13-1", new Resolver(),
            "ALLSTARS", NullLogger.Instance, null, repository, "2026-11-10");
        Assert.Equal(EigenTeamBesluit.Handmatig, result.Besluit);
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("JO13-1")]
    [InlineData("MO13-1")]
    public async Task ResolverFailureOnEitherSide_IsManualAndCannotReachReplyContinuation(string failingText)
    {
        var fixtures = new Fixtures { Throw = true };
        var classification = new BerichtClassificatie { TeamNaam = "JO13-1", Tegenstander = "MO13-1", Datum = "2026-11-10" };
        Assert.False(await EigenTeamBepaling.BepaalEnPasToeAsync(classification, new Resolver(failingText),
            "ALLSTARS", NullLogger.Instance, null, fixtures));
        Assert.True(classification.TeamHandmatigBeoordelen);
        Assert.Equal(0, fixtures.Calls);
        var graph = new FakeReplyGraph { ThrowOnSendReply = true };
        var persistence = new RecordingReplyPersistentie { ThrowOnMarkeerVerzendPoging = true };
        await TeamHandmatigeAfhandeling.HandelOfVervolgAsync(classification, 1, "message", persistence, graph,
            () => throw new InvalidOperationException("A lookup failure cannot reach templates, planning or sending"), NullLogger.Instance);
        Assert.Equal(EmailStatus.Review, Assert.Single(persistence.StatusUpdates).Status);
        Assert.Empty(persistence.VerzendPogingMarkeringen);
        Assert.Empty(graph.SentReplies);
    }

    private static Task<EigenTeamUitkomst> Determine(Fixtures repository, string? date, string? time)
        => EigenTeamBepaling.BepaalEigenTeamEnTegenstanderAsync("JO13-1", "MO13-1", new Resolver(),
            "ALLSTARS", NullLogger.Instance, null, repository, date, time);

    private sealed class Fixtures : ITeamFixtureContextRepository
    {
        public bool Throw { get; init; }
        public int Calls { get; private set; }
        public IReadOnlyList<TeamFixture> Rows { get; init; } = [];
        public Task<TeamFixtureContext> GetAsync(string clubCode, DateOnly date, IReadOnlyList<int> candidateIds)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("Unavailable");
            return Task.FromResult(new TeamFixtureContext(Rows, Names));
        }
    }

    private sealed class Resolver(string? failingText = null) : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
        {
            if (request.RuweTeamTekst == failingText) throw new InvalidOperationException("Unavailable");
            return Task.FromResult(request.RuweTeamTekst == "13-1"
                ? new TeamResolutionResult(null, null, 0, [new(1, "JO13-1", "JO13"), new(2, "MO13-1", "MO13")], ResolutionBron.MeerdereKandidaten)
                : new TeamResolutionResult(request.RuweTeamTekst == "JO13-1" ? 1 : 2, request.RuweTeamTekst, 1, [], ResolutionBron.ExacteMatch));
        }
    }
}
