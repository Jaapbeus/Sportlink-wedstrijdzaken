using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Planner.Shared.TeamResolution;
using SportlinkFunction.Email;
using SportlinkFunction.Processing;
using Xunit;

namespace FunctionApp.Tests.Processing;

/// <summary>
/// #1716 slice 4: het tier-omhulsel <see cref="BerichtPipeline.BepaalEigenTeamEnTegenstanderAsync"/> op de
/// SQL Server-tier. De bepaling zelf staat in <c>Planner.Shared</c> (<c>EigenTeamBepaling</c>) en is daar getest
/// (<c>Planner.Shared.Tests/TeamResolution/EigenTeamBepalingTests.cs</c>). Hier alleen: het omhulsel zet
/// de uitkomst terug op de classificatie, en laat die ongewijzigd als er niets herkend is. Geen database
/// nodig. Tegenhanger: <c>FunctionApp.Postgres.Tests/Processing/BerichtPipelineEigenTeamTests.cs</c>.
/// </summary>
public class BerichtPipelineEigenTeamTests
{
    private const string Club = "TESTCLUB";

    [Fact]
    public async Task Omhulsel_TeamInTegenstanderveld_ZetBeideVeldenOpDeClassificatie()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, TeamNaam = "Tegenstander JO13-1", Tegenstander = "JO13-2" };

        var herkend = await BerichtPipeline.BepaalEigenTeamEnTegenstanderAsync(
            classificatie, new VasteResolver("JO13-2"), Club, NullLogger.Instance, null);

        herkend.Should().BeTrue();
        classificatie.TeamNaam.Should().Be("TESTCLUB JO13-2");
        classificatie.Tegenstander.Should().Be("Tegenstander JO13-1");
    }

    [Fact]
    public async Task Omhulsel_NietsHerkend_LaatClassificatieOngewijzigd()
    {
        var classificatie = new BerichtClassificatie { Type = VerzoekType.BeschikbaarheidCheck, TeamNaam = " 13-1 ", Tegenstander = " Onbekend " };

        var herkend = await BerichtPipeline.BepaalEigenTeamEnTegenstanderAsync(
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
            NullLogger.Instance, new ConflictResolver(), Club, fixtureContext: fixtures);
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
            NullLogger.Instance, new ConflictResolver(true), Club, fixtureContext: new NoFixtures());
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
            NullLogger.Instance, new ConflictResolver(), Club, fixtureContext: new ConflictingFixtures());
        Assert.True(classification.TeamHandmatigBeoordelen);
        Assert.Contains("handmatigeTeamBeoordeling", result);
        Assert.False(classification.VoegKnvbPdfBijlageToe);
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
