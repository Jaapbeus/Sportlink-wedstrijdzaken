using FluentAssertions;
using Planner.Shared;
using SportlinkFunction.TeamResolution;
using Xunit;

namespace FunctionApp.Tests.TeamResolution;

public class TeamResolverTests
{
    private const string Club = "TESTCLUB";

    private static TeamResolutionRequest Request(string ruweTekst) => new(ruweTekst, null, null, Club);

    [Fact]
    public async Task ResolveAsync_GevalideerdeAlias_GeeftHoogsteBetrouwbaarheidTerug()
    {
        var fake = new FakeTeamCandidateRepository
        {
            ValidatedAlias = new TeamCandidate(42, "TESTCLUB O13-2", "JO13"),
        };
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("de tijgertjes"));

        resultaat.TeamId.Should().Be(42);
        resultaat.CanoniekeTeamnaam.Should().Be("TESTCLUB O13-2");
        resultaat.Confidence.Should().Be(1.0);
        resultaat.Bron.Should().Be(ResolutionBron.ExacteAlias);
    }

    [Fact]
    public async Task ResolveAsync_ExacteCanoniekeMatch_ZonderAlias_GeeftMatchTerug()
    {
        var fake = new FakeTeamCandidateRepository { ExactTeam = new TeamCandidate(7, "TESTCLUB O13-1", "JO13") };
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("JO13-1"));

        resultaat.TeamId.Should().Be(7);
        resultaat.Bron.Should().Be(ResolutionBron.ExacteMatch);
    }

    [Fact]
    public async Task ResolveAsync_ZoektMetGestripteClubPrefix()
    {
        // Een tegenstander schrijft de KNVB-vorm mét clubprefix; de canonieke sleutel is
        // prefixloos. De resolver moet daarop zoeken, niet op de ruwe tekst.
        var fake = new FakeTeamCandidateRepository { ExactTeam = new TeamCandidate(7, "TESTCLUB O13-1", "JO13") };
        var resolver = new TeamResolver(fake);

        await resolver.ResolveAsync(Request("TESTCLUB O13-1"));

        fake.LaatsteExactSleutel.Should().Be("JO13-1");
    }

    [Fact]
    public async Task ResolveAsync_EenKandidaatUitLeeftijdEnNummer_WordtOpgelost()
    {
        // Club heeft alleen JO13-1 (geen MO13-1) — de ambiguïteit lost zichzelf op zonder gok.
        var fake = new FakeTeamCandidateRepository
        {
            Kandidaten = [new TeamCandidate(9, "TESTCLUB O13-1", "JO13")],
        };
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("13-1"));

        resultaat.TeamId.Should().Be(9);
        resultaat.Bron.Should().Be(ResolutionBron.ExacteMatch);
        resultaat.Confidence.Should().BeLessThan(1.0);
    }

    [Fact]
    public async Task ResolveAsync_MeerdereKandidatenZonderDisambiguator_GeeftKandidatenTerugZonderGok()
    {
        var fake = new FakeTeamCandidateRepository
        {
            Kandidaten = [new TeamCandidate(9, "TESTCLUB O13-1", "JO13"), new TeamCandidate(10, "TESTCLUB MO13-1", "MO13")],
        };
        var resolver = new TeamResolver(fake);

        var resultaat = await resolver.ResolveAsync(Request("13-1"));

        resultaat.TeamId.Should().BeNull();
        resultaat.Bron.Should().Be(ResolutionBron.MeerdereKandidaten);
        resultaat.Kandidaten.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResolveAsync_MeerdereKandidatenMetDisambiguator_GebruiktDeKeuze()
    {
        var fake = new FakeTeamCandidateRepository
        {
            Kandidaten = [new TeamCandidate(9, "TESTCLUB O13-1", "JO13"), new TeamCandidate(10, "TESTCLUB MO13-1", "MO13")],
        };
        var resolver = new TeamResolver(fake, new FakeDisambiguator(10));

        var resultaat = await resolver.ResolveAsync(Request("13-1"));

        resultaat.TeamId.Should().Be(10);
        resultaat.Bron.Should().Be(ResolutionBron.AiDisambiguatie);
        resultaat.Confidence.Should().BeInRange(0.5, 0.9);
    }

    [Fact]
    public async Task ResolveAsync_DisambiguatorKiestBuitenKandidatenlijst_WordtGenegeerd()
    {
        // Harde validatie: een keuze die niet in de aangeboden lijst staat mag nooit een TeamId opleveren.
        var fake = new FakeTeamCandidateRepository
        {
            Kandidaten = [new TeamCandidate(9, "TESTCLUB O13-1", "JO13"), new TeamCandidate(10, "TESTCLUB MO13-1", "MO13")],
        };
        var resolver = new TeamResolver(fake, new FakeDisambiguator(999));

        var resultaat = await resolver.ResolveAsync(Request("13-1"));

        resultaat.TeamId.Should().BeNull();
        resultaat.Bron.Should().Be(ResolutionBron.MeerdereKandidaten);
    }

    [Fact]
    public async Task ResolveAsync_DisambiguatorKiestNiets_GeeftKandidatenTerug()
    {
        var fake = new FakeTeamCandidateRepository
        {
            Kandidaten = [new TeamCandidate(9, "TESTCLUB O13-1", "JO13"), new TeamCandidate(10, "TESTCLUB MO13-1", "MO13")],
        };
        var resolver = new TeamResolver(fake, new FakeDisambiguator(null));

        var resultaat = await resolver.ResolveAsync(Request("13-1"));

        resultaat.TeamId.Should().BeNull();
        resultaat.Bron.Should().Be(ResolutionBron.MeerdereKandidaten);
    }

    [Fact]
    public async Task ResolveAsync_GeenEnkeleKandidaat_GeeftOnopgelostTerug()
    {
        var resolver = new TeamResolver(new FakeTeamCandidateRepository());

        var resultaat = await resolver.ResolveAsync(Request("JO99-9"));

        resultaat.Should().Be(TeamResolutionResult.Onopgelost);
    }

    [Fact]
    public async Task ResolveAsync_VrijeTekst_RaadpleegtGeenKandidaten()
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
        public string? LaatsteExactSleutel { get; private set; }

        public Task<TeamCandidate?> FindValidatedAliasAsync(
            string clubCode, string ruweTekst, string genormaliseerdeSleutel)
            => Task.FromResult(ValidatedAlias);

        public Task<bool> HeeftActieveTeamsAsync(string clubCode) => Task.FromResult(true);

        public Task<TeamCandidate?> FindExactTeamAsync(string clubCode, string genormaliseerdeSleutel)
        {
            LaatsteExactSleutel = genormaliseerdeSleutel;
            return Task.FromResult(ExactTeam);
        }

        public Task<IReadOnlyList<TeamCandidate>> FindKandidatenAsync(string clubCode, TeamNaamComponenten componenten)
        {
            KandidatenOpgevraagd = true;
            return Task.FromResult(Kandidaten);
        }
    }

    private sealed class FakeDisambiguator(int? keuze) : ITeamDisambiguator
    {
        public Task<int?> KiesAsync(string ruweTekst, IReadOnlyList<TeamCandidate> kandidaten, CancellationToken ct = default)
            => Task.FromResult(keuze);
    }
}
