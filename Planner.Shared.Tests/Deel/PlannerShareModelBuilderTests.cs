using AwesomeAssertions;
using Planner.Shared.Deel;
using Xunit;

namespace Planner.Shared.Tests.Deel;

/// <summary>
/// #1363: de pure mapping van veldbezettings- en auto-plan-regels naar het deelmodel.
/// </summary>
public class PlannerShareModelBuilderTests
{
    private static readonly DateOnly Zaterdag = new(2026, 10, 3);

    private sealed record Bezetting(
        string? AanvangsTijd, string TeamNaam, string Wedstrijd, string? Uitteam,
        string? Veld, string? Competitiesoort) : IVeldbezettingRegel;

    private sealed record Plan(
        string TeamNaam, string Wedstrijd, string? Competitiesoort,
        string? HuidigeTijd, string? HuidigeVeld, string? OptimaalTijd, string? OptimaalVeld) : IPlanWedstrijdRegel;

    [Fact]
    public void VanVeldbezetting_NeemtDeKolommenOverDiePlanningVandaagToont()
    {
        var model = PlannerShareModelBuilder.VanVeldbezetting(
            new[] { new Bezetting("09:30", "JO10-1", "AllStars JO10-1 - Gasten JO10-2", "Gasten JO10-2", "veld 3 A", "competitie") },
            Zaterdag, "ALLSTARS");

        model.Titel.Should().Be("Veldbezetting op zaterdag 3 oktober 2026");
        model.ClubCode.Should().Be("ALLSTARS");
        model.Peildatum.Should().Be(Zaterdag);
        model.Wedstrijden.Should().ContainSingle().Which.Should().Be(
            new PlannerShareWedstrijd("09:30", "JO10-1", "Gasten JO10-2", "veld 3 A", "competitie", null));
    }

    [Fact]
    public void VanVeldbezetting_SorteertOpTijd_EnZetWedstrijdenZonderTijdAchteraan()
    {
        var model = PlannerShareModelBuilder.VanVeldbezetting(
            new[]
            {
                new Bezetting(null, "JO8-1", "w", null, null, null),
                new Bezetting("11:00", "JO12-1", "w", null, null, null),
                new Bezetting("08:45", "JO9-1", "w", null, null, null),
                new Bezetting("  ", "JO7-1", "w", null, null, null),
            },
            Zaterdag, "ALLSTARS");

        model.Wedstrijden.Select(w => w.Team).Should().Equal("JO9-1", "JO12-1", "JO7-1", "JO8-1");
        model.Wedstrijden.Skip(2).Select(w => w.Tijd).Should().AllBe(PlannerShareModelBuilder.GeenTijd);
    }

    [Fact]
    public void VanVeldbezetting_LegeEnWitruimteWaardenWordenNull()
    {
        var model = PlannerShareModelBuilder.VanVeldbezetting(
            new[] { new Bezetting("10:00", "JO11-1", "w", "  ", "", null) }, Zaterdag, "ALLSTARS");

        var regel = model.Wedstrijden.Single();
        regel.Tegenstander.Should().BeNull();
        regel.Veld.Should().BeNull();
        regel.Competitie.Should().BeNull();
    }

    [Fact]
    public void VanVeldbezetting_LegeLijst_GeeftLeegModelMetTitel()
    {
        var model = PlannerShareModelBuilder.VanVeldbezetting(Array.Empty<IVeldbezettingRegel>(), Zaterdag, "ALLSTARS");

        model.Wedstrijden.Should().BeEmpty();
        model.Titel.Should().Be("Veldbezetting op zaterdag 3 oktober 2026");
    }

    [Theory]
    [InlineData(PlanWeergave.Huidig, "Huidige planning op zaterdag 3 oktober 2026", "10:00", "veld 1")]
    [InlineData(PlanWeergave.Optimaal, "Optimale planning op zaterdag 3 oktober 2026", "09:15", "veld 2 B")]
    public void VanPlan_KiestDeKantVanDeGekozenTab_EnZegtDatInDeTitel(
        PlanWeergave weergave, string titel, string tijd, string veld)
    {
        var model = PlannerShareModelBuilder.VanPlan(
            new[] { new Plan("JO13-2", "AllStars JO13-2 - Gasten JO13-1", "beker", "10:00", "veld 1", "09:15", "veld 2 B") },
            Zaterdag, "ALLSTARS", weergave);

        model.Titel.Should().Be(titel);
        model.Wedstrijden.Should().ContainSingle().Which.Should().Be(
            new PlannerShareWedstrijd(tijd, "JO13-2", "Gasten JO13-1", veld, "beker", null));
    }

    [Fact]
    public void VanPlan_NietInplanbaar_KrijgtPlaatshouderInPlaatsVanLegeTijd()
    {
        var model = PlannerShareModelBuilder.VanPlan(
            new[] { new Plan("MO15-1", "w", null, "10:00", "veld 1", null, null) },
            Zaterdag, "ALLSTARS", PlanWeergave.Optimaal);

        model.Wedstrijden.Single().Tijd.Should().Be(PlannerShareModelBuilder.GeenTijd);
        model.Wedstrijden.Single().Veld.Should().BeNull();
    }

    [Theory]
    [InlineData("AllStars JO10-1 - Gasten JO10-2", "Gasten JO10-2")]
    [InlineData("JO10-1 - JO10-2", "JO10-2")]
    [InlineData("AllStars JO10-1", null)]            // geen scheiding
    [InlineData("A - B - C", null)]                   // niet eenduidig: nooit gokken
    [InlineData("AllStars JO10-1 - ", null)]          // lege tegenstander
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TegenstanderUitWedstrijd_SplitstAlleenEenduidig(string? wedstrijd, string? verwacht)
    {
        PlannerShareModelBuilder.TegenstanderUitWedstrijd(wedstrijd).Should().Be(verwacht);
    }

    [Fact]
    public void Builders_WeigerenNull()
    {
        var bezetting = () => PlannerShareModelBuilder.VanVeldbezetting(null!, Zaterdag, "ALLSTARS");
        var plan = () => PlannerShareModelBuilder.VanPlan(null!, Zaterdag, "ALLSTARS", PlanWeergave.Huidig);

        bezetting.Should().Throw<ArgumentNullException>();
        plan.Should().Throw<ArgumentNullException>();
    }
}
