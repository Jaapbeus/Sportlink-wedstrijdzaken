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

    // #1863: een O23-team dat Sportlink als "Senioren" (1-99) levert, bezet het veld met de JO23-duur;
    // zonder bruikbare JO23-rij (duur 0) blijft de seniorenduur gelden, en "MO23" is geen O23-team.
    [Theory]
    [InlineData("ALLSTARS O23-1", 105, "JO23", 11, 45)]
    [InlineData("ALLSTARS O23-1", 0, "1-99", 11, 30)]
    [InlineData("ALLSTARS MO23-1", 105, "1-99", 11, 30)]
    public void O23Team_GebruiktJo23SpeeltijdInDeBezetting(string team, int jo23, string sleutel, int eindUur, int eindMinuut)
    {
        var match = new SportlinkProgrammaMatch
        {
            Teamnaam = team,
            Accommodatie = "Test accommodatie",
            Status = "Vastgesteld",
            Aanvangstijd = "10:00",
            Veld = "Veld 1 A"
        };
        var result = SportlinkApiClient.TryMapToBezetting(
            match, new DateOnly(2026, 9, 5), "Test accommodatie", "ALLSTARS",
            [("Veld 1", 1)],
            new Dictionary<string, Speeltijd>
            {
                ["JO23"] = new() { Leeftijd = "JO23", WedstrijdTotaal = jo23, Veldafmeting = 1m },
                ["1-99"] = new() { Leeftijd = "1-99", WedstrijdTotaal = 90, Veldafmeting = 1m },
            },
            new Dictionary<string, string> { [team] = "1-99" });

        result.Should().NotBeNull();
        result!.LeeftijdsCategorie.Should().Be(sleutel);
        result.EindTijd.Should().Be(new TimeOnly(eindUur, eindMinuut));
    }

    // #1814: de DB-terugval mag de planningsblokkade niet opslokken; een echte API-storing wel terugvallen.
    [Fact]
    public void UnknownDuration_IsNotSwallowedByDatabaseFallback()
    {
        SportlinkApiClient.MagTerugvallenOpDatabase(new PlanningDuurOnbekendException()).Should().BeFalse();
        SportlinkApiClient.MagTerugvallenOpDatabase(new HttpRequestException("api down")).Should().BeTrue();
    }
}
