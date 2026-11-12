using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Planner.Shared.TeamResolution;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Processing;
using Xunit;

namespace FunctionApp.Postgres.Tests.Processing;

/// <summary>
/// Verifies shared classification preparation and the tier pipeline's terminal manual-review boundary.
/// These tests use synthetic resolvers and fixture sources without a database.
/// </summary>
public class BerichtPipelineEigenTeamTests
{
    private const string Club = "TESTCLUB";

    [Fact]
    public async Task Omhulsel_TeamInTegenstanderveld_ZetBeideVeldenOpDeClassificatie()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, TeamNaam = "Tegenstander JO13-1", Tegenstander = "JO13-2" };

        var herkend = await EigenTeamBepaling.BereidPlannerVoorAsync(
            classificatie, new VasteResolver("JO13-2"), Club, NullLogger.Instance, null);

        herkend.Should().BeTrue();
        classificatie.TeamNaam.Should().Be("TESTCLUB JO13-2");
        classificatie.Tegenstander.Should().Be("Tegenstander JO13-1");
    }

    [Fact]
    public async Task Omhulsel_NietsHerkend_LaatClassificatieOngewijzigd()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, TeamNaam = " 13-1 ", Tegenstander = " Onbekend " };

        var herkend = await EigenTeamBepaling.BereidPlannerVoorAsync(
            classificatie, new VasteResolver("JO13-2"), Club, NullLogger.Instance, null);

        herkend.Should().BeFalse();
        classificatie.TeamNaam.Should().Be(" 13-1 ");
        classificatie.Tegenstander.Should().Be(" Onbekend ");
    }

    [Theory]
    [InlineData(VerzoekType.BeschikbaarheidCheck, false)]
    [InlineData(VerzoekType.BeschikbaarheidCheck, true)]
    [InlineData(VerzoekType.HerplanVerzoek, false)]
    [InlineData(VerzoekType.HerplanVerzoek, true)]
    [InlineData(VerzoekType.TeamContactOpvragen, false)]
    [InlineData(VerzoekType.TeamContactOpvragen, true)]
    public async Task ConflictingTeams_ManualOutcomeStopsEveryBranchBeforeDatabaseOrMutation(VerzoekType type, bool validDate)
    {
        var classification = new BerichtClassificatie { Type = type, TeamNaam = "JO13-1", Tegenstander = "MO13-1", Datum = validDate ? "2026-11-10" : null };
        var fixtures = new NoFixtures();
        var result = await BerichtPipeline.VerwerkMetPlannerAsync(classification, new InkomendBericht(),
            NullLogger.Instance, new ConflictResolver(), Club, fixtureContextFactory: () => fixtures);
        Assert.Equal(validDate ? 1 : 0, fixtures.Calls);
        Assert.True(classification.TeamHandmatigBeoordelen);
        Assert.True(classification.TeamNaamOnbevestigd);
        Assert.Contains("handmatigeTeamBeoordeling", result);
        Assert.Equal("JO13-1", classification.TeamNaam);
        Assert.Equal("MO13-1", classification.Tegenstander);
        Assert.False(classification.VoegKnvbPdfBijlageToe);
    }

    [Theory]
    [InlineData(VerzoekType.BeschikbaarheidCheck)]
    [InlineData(VerzoekType.HerplanVerzoek)]
    [InlineData(VerzoekType.TeamContactOpvragen)]
    public async Task SecondTeamLookupFailure_IsTerminalBeforeDatabaseOrMutation(VerzoekType type)
    {
        var classification = new BerichtClassificatie { Type = type, TeamNaam = "JO13-1", Tegenstander = "MO13-1" };
        var result = await BerichtPipeline.VerwerkMetPlannerAsync(classification, new InkomendBericht(),
            NullLogger.Instance, new ConflictResolver(true), Club, fixtureContextFactory: () => new NoFixtures());
        Assert.True(classification.TeamHandmatigBeoordelen);
        Assert.Contains("handmatigeTeamBeoordeling", result);
        Assert.False(classification.VoegKnvbPdfBijlageToe);
    }

    [Theory]
    [InlineData(VerzoekType.BeschikbaarheidCheck)]
    [InlineData(VerzoekType.HerplanVerzoek)]
    [InlineData(VerzoekType.TeamContactOpvragen)]
    public async Task AnotherFixtureForSelectedOwner_IsTerminalBeforeDownstreamFirstMatchLookup(VerzoekType type)
    {
        var classification = new BerichtClassificatie { Type = type, TeamNaam = "JO13-1", Tegenstander = "MO13-1",
            Datum = "2026-11-10", AanvangsTijd = "10:30" };
        var result = await BerichtPipeline.VerwerkMetPlannerAsync(classification, new InkomendBericht(),
            NullLogger.Instance, new ConflictResolver(), Club, fixtureContextFactory: () => new ConflictingFixtures());
        Assert.True(classification.TeamHandmatigBeoordelen);
        Assert.Contains("handmatigeTeamBeoordeling", result);
        Assert.False(classification.VoegKnvbPdfBijlageToe);
    }

    // #1862: de afzender-opzoeking is aangesloten op de pipeline van deze tier. Een fake bron vervangt de
    // database; een afzender bij twee teams geeft de wedervraag zonder dat er een plannerquery volgt.
    [Fact]
    public async Task AfzenderBijMeerdereTeams_GeeftTeamOnbekendZonderDatabase()
    {
        var bron = new VasteAfzenderBron("JO13-2", "JO13-1");
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        var result = await BerichtPipeline.VerwerkMetPlannerAsync(classificatie, new InkomendBericht { Afzender = "trainer@voorbeeld.nl" },
            NullLogger.Instance, new VasteResolver("JO13-2"), Club, afzenderTeamBron: bron);

        bron.Aanroepen.Should().Be(1);
        classificatie.TeamAfgeleidUitAfzender.Should().BeFalse();
        classificatie.TeamNaam.Should().BeNull();
        result.Should().Contain("teamOnbekend");
    }

    [Fact]
    public async Task AntwoordadresWijktAf_PipelineDoetGeenAfzenderOpzoeking()
    {
        var bron = new VasteAfzenderBron("JO13-2");
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        var result = await BerichtPipeline.VerwerkMetPlannerAsync(classificatie,
            new InkomendBericht { Afzender = "trainer@voorbeeld.nl", InternetHeaders = [new("Reply-To", "Ander <ander@voorbeeld.nl>")] },
            NullLogger.Instance, new VasteResolver("JO13-2"), Club, afzenderTeamBron: bron);

        bron.Aanroepen.Should().Be(0);
        result.Should().Contain("teamOnbekend");
    }

    [Fact]
    public async Task Omhulsel_AfzenderBegeleidtPreciesEenTeam_LeidtHetTeamAf()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck };

        var herkend = await EigenTeamBepaling.BereidPlannerVoorAsync(classificatie, new VasteResolver("JO13-2"), Club,
            NullLogger.Instance, null, afzender: new AfzenderOpzoeking(new VasteAfzenderBron("JO13-2"), "trainer@voorbeeld.nl", []));

        herkend.Should().BeTrue();
        classificatie.TeamNaam.Should().Be("TESTCLUB JO13-2");
        classificatie.TeamAfgeleidUitAfzender.Should().BeTrue();
    }

    [Fact]
    public async Task Omhulsel_Seniorenteam_KrijgtCategorieUitDeTeamtabel()
    {
        // #1863: het seniorenteam heeft geen leeftijd in de naam; de categorie komt van de resolver (teamtabel van de tier).
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, TeamNaam = "1" };
        var resolver = new SeniorenResolver();

        var herkend = await EigenTeamBepaling.BereidPlannerVoorAsync(classificatie, resolver, Club, NullLogger.Instance, null);

        herkend.Should().BeTrue();
        classificatie.TeamNaam.Should().Be("TESTCLUB 1");
        classificatie.LeeftijdsCategorie.Should().Be("1-99");
    }

    private sealed class SeniorenResolver : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
            => Task.FromResult(request.RuweTeamTekst == "1"
                ? new TeamResolutionResult(3, "TESTCLUB 1", 1.0, [], ResolutionBron.ExacteMatch, "1-99")
                : TeamResolutionResult.Onopgelost);
    }

    private sealed class VasteAfzenderBron(params string[] teams) : IAfzenderTeamBron
    {
        public int Aanroepen { get; private set; }
        public Task<IReadOnlyList<string>> ZoekTeamsVanAfzenderAsync(string afzender, string clubCode)
        {
            Aanroepen++;
            return Task.FromResult<IReadOnlyList<string>>(teams);
        }
    }

    private sealed class ConflictingFixtures : ITeamFixtureContextRepository
    {
        public Task<TeamFixtureContext> GetAsync(string clubCode, DateOnly date, IReadOnlyList<int> candidateIds)
            => Task.FromResult(new TeamFixtureContext(
                [new("supported", "JO13-1", "JO13-1", "MO13-1", "10:30"),
                 new("earlier", "JO13-1", "JO13-1", "external", "09:00")],
                [new(1, "JO13-1"), new(2, "MO13-1")]));
    }

    private sealed class ConflictResolver(bool failOpponent = false) : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
        {
            if (failOpponent && request.RuweTeamTekst == "MO13-1") throw new InvalidOperationException("Unavailable");
            return Task.FromResult(new TeamResolutionResult(request.RuweTeamTekst == "JO13-1" ? 1 : 2,
                request.RuweTeamTekst, 1, [], ResolutionBron.ExacteMatch));
        }
    }

    private sealed class NoFixtures : ITeamFixtureContextRepository
    {
        public int Calls { get; private set; }
        public Task<TeamFixtureContext> GetAsync(string clubCode, DateOnly date, IReadOnlyList<int> candidateIds)
        {
            Calls++;
            return Task.FromResult(new TeamFixtureContext([], []));
        }
    }

    /// <summary>Lost alleen de opgegeven tekst op, naar "TESTCLUB " + tekst.</summary>
    private sealed class VasteResolver(string bekend) : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
            => Task.FromResult(request.RuweTeamTekst == bekend
                ? new TeamResolutionResult(7, "TESTCLUB " + bekend, 1.0, [], ResolutionBron.ExacteMatch)
                : TeamResolutionResult.Onopgelost);
    }
}
