using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// #1547: de bezettingsduur op de Planning volgt Sportlink. De verwachte waarden zijn nagemeten op de
/// Sportlink-veldplanner van één speeldag: elk blok is de netto speelduur plus vijftien minuten.
/// </summary>
public class VeldbezettingDuurTests
{
    [Theory]
    [InlineData(50, 65)]   // 2×25, ook waar de eigen speeltijdentabel tien minuten rust kent
    [InlineData(60, 75)]
    [InlineData(70, 85)]
    [InlineData(80, 95)]
    [InlineData(90, 105)]
    public void SportlinkSpeelduur_IsLeidend_PlusVijftienMinuten(int sportlink, int verwacht)
    {
        VeldbezettingDuur.Bepaal(sportlink, speeltijdenTotaal: 115).Should().Be(verwacht);
    }

    [Theory]
    [InlineData(20, 35)]   // 35+/VR30+: één keer 20 minuten; de rust is geen speeltijd en er is geen uitzondering (#1561)
    [InlineData(40, 55)]
    public void KorteWedstrijd_VolgtDezelfdeRegelAlsElkeAndere(int sportlink, int verwacht)
    {
        VeldbezettingDuur.Bepaal(sportlink, speeltijdenTotaal: 115).Should().Be(verwacht);
    }

    [Fact]
    public void AfwijkendeSpeelduurVanEenTeam_WintVanDeCategorieStandaard()
    {
        // Een veteranenteam speelt 2×30, de seniorencategorie staat op 115 minuten.
        VeldbezettingDuur.Bepaal(60, 115).Should().Be(75);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void ZonderSportlinkSpeelduur_ValtTerugOpSpeeltijden(int? sportlink)
    {
        VeldbezettingDuur.Bepaal(sportlink, 85).Should().Be(85);
    }

    [Fact]
    public void ZonderEnigeBron_IsDeDuurNul()
    {
        // 0 betekent: geen Gantt-blok, wél een regel in de lijst eronder.
        VeldbezettingDuur.Bepaal(null, null).Should().Be(0);
    }
}
