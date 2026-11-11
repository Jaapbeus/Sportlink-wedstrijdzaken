using AwesomeAssertions;
using Planner.Shared;
using Planner.Shared.Planning;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

public class SportlinkApiClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolvedApiFieldWithoutDuration_ThrowsTypedPlanningBlock(bool teamMapped)
    {
        var teamLookup = teamMapped
            ? new Dictionary<string, string> { ["ALLSTARS JO13-1"] = "JO13" }
            : new Dictionary<string, string>();
        var durationLookup = new Dictionary<string, Speeltijd>();
        var match = new SportlinkProgrammaMatch
        {
            Teamnaam = "ALLSTARS JO13-1",
            Accommodatie = "Test accommodatie",
            Status = "Vastgesteld",
            Aanvangstijd = "10:00",
            Veld = "Veld 1 A"
        };

        var map = () => SportlinkApiClient.TryMapToBezetting(
            match, new DateOnly(2026, 9, 5), "Test accommodatie", "ALLSTARS",
            [("Veld 1", 1)], durationLookup, teamLookup);

        map.Should().Throw<PlanningDuurOnbekendException>();
    }

    [Fact]
    public void ResolvedApiFieldWithDuration_ReturnsOccupancy()
    {
        var match = new SportlinkProgrammaMatch
        {
            Teamnaam = "ALLSTARS JO13-1",
            Accommodatie = "Test accommodatie",
            Status = "Vastgesteld",
            Aanvangstijd = "10:00",
            Veld = "Veld 1 A"
        };
        var result = SportlinkApiClient.TryMapToBezetting(
            match, new DateOnly(2026, 9, 5), "Test accommodatie", "ALLSTARS",
            [("Veld 1", 1)],
            new Dictionary<string, Speeltijd> { ["JO13"] = new() { Leeftijd = "JO13", WedstrijdTotaal = 60, Veldafmeting = 1m } },
            new Dictionary<string, string> { ["ALLSTARS JO13-1"] = "JO13" });

        result.Should().NotBeNull();
        result!.VeldNummer.Should().Be(1);
        result.AanvangsTijd.Should().Be(new TimeOnly(10, 0));
        result.EindTijd.Should().Be(new TimeOnly(11, 0));
    }
}
