using AwesomeAssertions;
using Planner.Shared.Deel;
using Xunit;

namespace Planner.Shared.Tests.Deel;

public class PlannerShareHtmlGeneratorTests
{
    private static readonly DateOnly Zaterdag = new(2026, 10, 3);

    private static PlannerShareModel Model(params PlannerShareWedstrijd[] regels) =>
        new("Veldbezetting op zaterdag 3 oktober 2026", "ALLSTARS", Zaterdag, regels);

    [Fact]
    public void Genereer_ToontKoppenEnRegels_ZonderScheidsrechterKolomAlsDieLeegIs()
    {
        var html = PlannerShareHtmlGenerator.Genereer(
            Model(new PlannerShareWedstrijd("09:30", "JO10-1", "Gasten JO10-2", "veld 3 A", "competitie", null)));

        html.Should().Contain("Veldbezetting op zaterdag 3 oktober 2026")
            .And.Contain(">Tijd<").And.Contain(">Team<").And.Contain(">Tegenstander<")
            .And.Contain("09:30").And.Contain("veld 3 A")
            .And.NotContain("Scheidsrechter");
    }

    [Fact]
    public void Genereer_MetScheidsrechter_ToontDeKolom()
    {
        var html = PlannerShareHtmlGenerator.Genereer(
            Model(new PlannerShareWedstrijd("09:30", "JO10-1", null, null, null, "Scheids Een")));

        html.Should().Contain(">Scheidsrechter<").And.Contain("Scheids Een");
    }

    [Fact]
    public void Genereer_EncodeertGegevensUitSportlink()
    {
        var html = PlannerShareHtmlGenerator.Genereer(
            Model(new PlannerShareWedstrijd("09:30", "<script>alert(1)</script>", "A&B \"x\"", null, null, null)));

        html.Should().NotContain("<script>").And.Contain("&lt;script&gt;").And.Contain("A&amp;B");
    }

    [Fact]
    public void Genereer_ZonderWedstrijden_GeeftMelding()
    {
        var html = PlannerShareHtmlGenerator.Genereer(Model());

        html.Should().Contain("Geen wedstrijden gepland").And.NotContain("<table");
    }
}
