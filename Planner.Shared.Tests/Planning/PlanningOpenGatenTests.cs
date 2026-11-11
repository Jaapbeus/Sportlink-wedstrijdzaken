using AwesomeAssertions;
using Planner.Shared.Planning;
using Xunit;

namespace Planner.Shared.Tests.Planning;

public class PlanningOpenGatenTests
{
    [Fact]
    public void OverlappendeBezettingen_LatenGeenGatTeruglopen()
    {
        var gaps = PlanningOpenGaten.Vind(new TimeOnly(8, 0), new TimeOnly(14, 0),
        [
            new BestaandeWedstrijd { AanvangsTijd = new TimeOnly(9, 0), EindTijd = new TimeOnly(12, 0) },
            new BestaandeWedstrijd { AanvangsTijd = new TimeOnly(10, 0), EindTijd = new TimeOnly(10, 30) }
        ], 10);

        gaps.Should().ContainSingle(g => g.Van == new TimeOnly(12, 10));
        gaps.Should().NotContain(g => g.Van < new TimeOnly(12, 10) && g.Van > new TimeOnly(9, 0));
    }

    [Fact]
    public void BezettingenBuitenVenster_BegrenzenHetVensterNiet()
    {
        var gaps = PlanningOpenGaten.Vind(new TimeOnly(8, 30), new TimeOnly(12, 0),
        [
            new BestaandeWedstrijd { AanvangsTijd = new TimeOnly(18, 0), EindTijd = new TimeOnly(19, 0) }
        ], 10);

        gaps.Should().ContainSingle();
        gaps[0].Van.Should().Be(new TimeOnly(8, 30));
        gaps[0].Tot.Should().Be(new TimeOnly(12, 0));
    }
}
