using AwesomeAssertions;
using FunctionApp.Postgres.Planner;
using FunctionApp.Postgres.Planner.Repositories;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// WZ-ADR-014 (#1960): Veld optimalisatie rekent alleen met de speeltijden van de club en de buffer van de
/// pagina, nooit met Sportlinks speelduur. Tegenhanger van
/// <c>FunctionApp.Tests/Planner/AutoPlanSpeeltijdenContractTests</c>.
/// </summary>
public class AutoPlanSpeeltijdenContractTests
{
    private static readonly VeldInfo Veld = new() { VeldNummer = 1, VeldNaam = "veld 1" };

    private static List<AutoPlanWedstrijdItem> Plan(
        IEnumerable<(string Team, int? SportlinkSpeelduur, TimeOnly Doel)> wedstrijden,
        Dictionary<string, Speeltijd> speeltijden, int buffer,
        Dictionary<string, (int, int)>? teamBuffers = null)
    {
        teamBuffers ??= new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var scheduler = new FieldScheduler(
            new List<VeldBeschikbaarheidInfo>
            {
                new() { VeldNummer = 1, BeschikbaarVanaf = new TimeOnly(9, 0), BeschikbaarTot = new TimeOnly(18, 0) }
            },
            new List<VeldInfo> { Veld }, buffer, teamBuffers);
        long code = 1;
        var metDoel = wedstrijden.Select(w => (
            new WedstrijdRaw(code++, "Test", w.Team, null, "09:00", "veld 1", null, null, w.SportlinkSpeelduur),
            new PlanDoel(2, 0, w.Doel, null, "leeftijd", "JO13"))).ToList();

        return AutoPlanService.PlanWedstrijden(metDoel, scheduler, speeltijden,
            new Dictionary<int, VeldInfo> { [1] = Veld }, teamBuffers, buffer);
    }

    private static Dictionary<string, Speeltijd> JO13(int totaal) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["JO13"] = new Speeltijd { Leeftijd = "JO13", Veldafmeting = 1.00m, WedstrijdTotaal = totaal }
        };

    [Fact]
    public void DuurKomtUitSpeeltijden_NietUitSportlink()
    {
        var item = Plan([("JO13-1", 90, new TimeOnly(10, 0))], JO13(75), buffer: 15).Single();

        item.DuurMinuten.Should().Be(75);
    }

    [Fact]
    public void ZonderSpeeltijdenrij_IsOnbekendTeam_OokMetSportlinkSpeelduur()
    {
        var item = Plan([("JO13-1", 60, new TimeOnly(10, 0))],
            new Dictionary<string, Speeltijd>(StringComparer.OrdinalIgnoreCase), buffer: 15).Single();

        item.Status.Should().Be("onbekend-team");
    }

    [Theory]
    [InlineData(0, "11:15")]
    [InlineData(10, "11:25")]
    [InlineData(15, "11:30")]
    public void PaginabufferGeldtOokVoorWedstrijdenMetSportlinkSpeelduur(int buffer, string verwachteTweedeStart)
    {
        var items = Plan(
            [("JO13-1", 60, new TimeOnly(10, 0)), ("JO13-2", 60, new TimeOnly(11, 15))],
            JO13(75), buffer);

        items[0].OptimaalTijd.Should().Be("10:00");
        items[1].OptimaalTijd.Should().Be(verwachteTweedeStart);
    }

    [Fact]
    public void TeambufferVoor60_GeldtOokVoorEenWedstrijdMetSportlinkSpeelduur()
    {
        var teamBuffers = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase) { ["JO13-2"] = (60, 0) };

        var items = Plan(
            [("JO13-1", 60, new TimeOnly(10, 0)), ("JO13-2", 60, new TimeOnly(11, 15))],
            JO13(75), buffer: 15, teamBuffers);

        items[1].OptimaalTijd.Should().Be("12:15");
    }
}
