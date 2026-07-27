using FluentAssertions;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

public class TeamResolverTests
{
    private const string ClubCode = "VRC";

    private static TeamResolutionRequest Request(string ruweTekst) => new(ruweTekst, null, null, ClubCode);

    [Fact]
    public async Task ResolveAsync_GevalideerdeAlias_GeeftHoogsteBetrouwbaarheidTerug()
    {
        var fake = new FakeTeamCandidateRepository
        {
            ValidatedAlias = new TeamCandidate(42, "JO13-2", "JO13"),
        };
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("jo13/2"));

        resultaat.TeamId.Should().Be(42);
        resultaat.CanoniekeTeamnaam.Should().Be("JO13-2");
        resultaat.Confidence.Should().Be(1.0);
        resultaat.Bron.Should().Be(ResolutionBron.ExacteAlias);
    }

    [Fact]
    public async Task ResolveAsync_ExacteCanoniekeMatch_ZonderAlias_GeeftMatchTerug()
    {
        var fake = new FakeTeamCandidateRepository
        {
            ExactTeam = new TeamCandidate(7, "JO13-1", "JO13"),
        };
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("JO13-1"));

        resultaat.TeamId.Should().Be(7);
        resultaat.Bron.Should().Be(ResolutionBron.ExacteMatch);
    }

    [Fact]
    public async Task ResolveAsync_GeenAliasGeenExacteMatch_EenKandidaatUitLeeftijdEnNummer_WordtOpgelost()
    {
        // Scenario 4-vangnet: club heeft alleen JO13-1 (geen MO13-1) — ambiguïteit lost
        // zichzelf op zonder gok, puur doordat er maar 1 kandidaat is.
        var fake = new FakeTeamCandidateRepository
        {
            Kandidaten = [new TeamCandidate(9, "JO13-1", "JO13")],
        };
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("13-1"));

        resultaat.TeamId.Should().Be(9);
        resultaat.Bron.Should().Be(ResolutionBron.ExacteMatch);
        resultaat.Confidence.Should().BeLessThan(1.0);
    }

    [Fact]
    public async Task ResolveAsync_MeerdereKandidaten_GeenGok_KandidatenTerugAlsDisambiguatieInput()
    {
        // Scenario 4/16: club heeft zowel JO13-1 als MO13-1 (of "JO13" matcht meerdere teams) —
        // de resolver kiest NIET automatisch, in tegenstelling tot de oude LIKE-matching.
        var fake = new FakeTeamCandidateRepository
        {
            Kandidaten =
            [
                new TeamCandidate(9, "JO13-1", "JO13"),
                new TeamCandidate(10, "MO13-1", "MO13"),
            ],
        };
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("13-1"));

        resultaat.TeamId.Should().BeNull();
        resultaat.Bron.Should().Be(ResolutionBron.MeerdereKandidaten);
        resultaat.Kandidaten.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResolveAsync_GeenEnkeleKandidaat_GeeftOnopgelostTerug()
    {
        var fake = new FakeTeamCandidateRepository();
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("JO99-9"));

        resultaat.Should().Be(TeamResolutionResult.Onopgelost);
    }

    [Fact]
    public async Task ResolveAsync_NietTeamAchtigeVrijeTekst_GeeftOnopgelostTerugZonderRepositoryTeRaadplegen()
    {
        var fake = new FakeTeamCandidateRepository();
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("Kan de wedstrijd verplaatst worden?"));

        resultaat.Should().Be(TeamResolutionResult.Onopgelost);
        fake.KandidatenOpgevraagd.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_LegeClubCode_GooitArgumentException()
    {
        var resolver = new TeamResolver(new FakeTeamCandidateRepository());

        var act = async () => await resolver.ResolveAsync(new TeamResolutionRequest("JO13-1", null, null, ""));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FakeTeamCandidateRepository : ITeamCandidateRepository
    {
        public TeamCandidate? ValidatedAlias { get; set; }
        public TeamCandidate? ExactTeam { get; set; }
        public IReadOnlyList<TeamCandidate> Kandidaten { get; set; } = [];
        public bool KandidatenOpgevraagd { get; private set; }

        public Task<TeamCandidate?> FindValidatedAliasAsync(string clubCode, string genormaliseerdeSleutel)
            => Task.FromResult(ValidatedAlias);

        public Task<TeamCandidate?> FindExactTeamAsync(string clubCode, string genormaliseerdeSleutel)
            => Task.FromResult(ExactTeam);

        public Task<IReadOnlyList<TeamCandidate>> FindKandidatenAsync(string clubCode, TeamNaamComponenten componenten)
        {
            KandidatenOpgevraagd = true;
            return Task.FromResult(Kandidaten);
        }
    }
}
