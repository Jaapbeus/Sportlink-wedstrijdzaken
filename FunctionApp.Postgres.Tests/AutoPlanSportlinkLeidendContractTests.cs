using AwesomeAssertions;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Planner.Repositories;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// #1559 (review ronde 1, P2): een bestaande wedstrijd met Sportlink-duur hangt niet af van de
/// speeltijdentabel. Tegenhanger van <c>FunctionApp.Tests/Planner/AutoPlanSportlinkLeidendContractTests</c>.
/// </summary>
public class AutoPlanSportlinkLeidendContractTests
{
    private static List<AutoPlanWedstrijdItem> Plan(int? sportlinkSpeelduur)
    {
        var veld = new VeldInfo { VeldNummer = 1, VeldNaam = "veld 1" };
        var scheduler = new FieldScheduler(
            new List<VeldBeschikbaarheidInfo>
            {
                new() { VeldNummer = 1, BeschikbaarVanaf = new TimeOnly(9, 0), BeschikbaarTot = new TimeOnly(18, 0) }
            },
            new List<VeldInfo> { veld }, 15);
        var wedstrijd = new WedstrijdRaw(1, "Test", "Onbekend Team", null, "10:00", "veld 1", null, null, sportlinkSpeelduur);
        var doel = new PlanDoel(3, 0, new TimeOnly(10, 0), null, null, "ONBEKEND");

        return AutoPlanService.PlanWedstrijden(
            new List<(WedstrijdRaw, PlanDoel)> { (wedstrijd, doel) }, scheduler,
            new Dictionary<string, Speeltijd>(), new Dictionary<int, VeldInfo> { [1] = veld },
            new Dictionary<string, (int, int)>(), 15);
    }

    [Fact]
    public void SportlinkDuurZonderSpeeltijdenrij_WordtIngepland_MetSportlinkDuurPlusVijftien()
    {
        var item = Plan(60).Single();

        item.Status.Should().NotBe("onbekend-team");
        item.DuurMinuten.Should().Be(75);
        item.OptimaalTijd.Should().Be("10:00");
    }

    [Fact]
    public void ZonderSportlinkDuurEnZonderSpeeltijdenrij_BlijftOnbekendTeam()
    {
        Plan(null).Single().Status.Should().Be("onbekend-team");
    }
}
