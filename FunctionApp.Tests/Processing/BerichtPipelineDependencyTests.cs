using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Planner.Shared.TeamResolution;
using SportlinkFunction.Email;
using SportlinkFunction.Processing;
using Xunit;

namespace FunctionApp.Tests.Processing;

/// <summary>These default-wiring paths must work in a process with no database configuration.</summary>
public sealed class BerichtPipelineDependencyTests
{
    [Theory]
    [InlineData(VerzoekType.BeschikbaarheidCheck, "JO13-1", null, "datumOnbekend")]
    [InlineData(VerzoekType.BeschikbaarheidCheck, "JO13-1", "invalid", "datumOnbekend")]
    [InlineData(VerzoekType.HerplanVerzoek, null, "2026-11-10", "Onvoldoende gegevens")]
    [InlineData(VerzoekType.HerplanVerzoek, "JO13-1", null, "Onvoldoende gegevens")]
    [InlineData(VerzoekType.HerplanVerzoek, "JO13-1", "2026-11-31", "Onvoldoende gegevens")]
    [InlineData(VerzoekType.TeamContactOpvragen, null, null, "coachGevonden\":false")]
    [InlineData(VerzoekType.Bevestiging, "JO13-1", null, "Bevestiging ontvangen")]
    [InlineData(VerzoekType.BuitenScope, "JO13-1", null, "Niet verwerkt")]
    public async Task DefaultWiring_QueryFreeInput_ReturnsWithoutDatabaseConfiguration(
        VerzoekType type, string? team, string? date, string expected)
    {
        var classification = new BerichtClassificatie { Type = type, TeamNaam = team, Datum = date };

        var response = await BerichtPipeline.VerwerkMetPlannerAsync(classification, new InkomendBericht(),
            NullLogger.Instance, new Resolver(), "ALLSTARS");

        Assert.Contains(expected, response);
        Assert.False(classification.TeamHandmatigBeoordelen);
    }

    [Theory]
    [InlineData("13-1", "2026-11-10")]
    [InlineData("failure", "2026-11-10")]
    [InlineData("JO13-1", null)]
    [InlineData("JO13-1", "invalid")]
    public async Task DefaultWiring_ManualDecision_StopsBeforeCreatingDatabaseDependency(string team, string? date)
    {
        var classification = new BerichtClassificatie
        {
            Type = VerzoekType.HerplanVerzoek, TeamNaam = team, Tegenstander = "MO13-1", Datum = date
        };

        var response = await BerichtPipeline.VerwerkMetPlannerAsync(classification, new InkomendBericht(),
            NullLogger.Instance, new Resolver(), "ALLSTARS");

        Assert.Contains("handmatigeTeamBeoordeling", response);
        Assert.True(classification.TeamHandmatigBeoordelen);
        Assert.Equal(team, classification.TeamNaam);
        Assert.Equal("MO13-1", classification.Tegenstander);
    }

    [Theory]
    [InlineData(VerzoekType.BeschikbaarheidCheck)]
    [InlineData(VerzoekType.HerplanVerzoek)]
    [InlineData(VerzoekType.TeamContactOpvragen)]
    public async Task FixtureFactoryFailure_StopsBeforePlanningOrContactDispatch(VerzoekType type)
    {
        var creations = 0;
        var classification = new BerichtClassificatie
        {
            Type = type, TeamNaam = "JO13-1", Tegenstander = "MO13-1", Datum = "2026-11-10"
        };

        var response = await BerichtPipeline.VerwerkMetPlannerAsync(classification, new InkomendBericht(),
            NullLogger.Instance, new Resolver(), "ALLSTARS",
            fixtureContextFactory: () => { creations++; throw new InvalidOperationException("Unavailable"); });

        Assert.Equal(1, creations);
        Assert.True(classification.TeamHandmatigBeoordelen);
        Assert.Contains("handmatigeTeamBeoordeling", response);
        Assert.Equal("JO13-1", classification.TeamNaam);
        Assert.Equal("MO13-1", classification.Tegenstander);
    }

    private sealed class Resolver : ITeamResolver
    {
        public Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request)
        {
            if (request.RuweTeamTekst == "failure") throw new InvalidOperationException("Unavailable");
            return Task.FromResult(request.RuweTeamTekst == "13-1"
                ? new TeamResolutionResult(null, null, 0,
                    [new(1, "JO13-1", "JO13"), new(2, "MO13-1", "MO13")], ResolutionBron.MeerdereKandidaten)
                : new TeamResolutionResult(request.RuweTeamTekst == "JO13-1" ? 1 : 2,
                    request.RuweTeamTekst, 1, [], ResolutionBron.ExacteMatch));
        }
    }
}
