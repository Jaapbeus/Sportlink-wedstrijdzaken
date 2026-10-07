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
        string? Veld, string? Competitiesoort, string? Tegenstander = null, bool NietInSportlink = false) : IVeldbezettingRegel;

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
    public void VanVeldbezetting_GebruiktDeTegenstanderVanDePagina_NietDeUitploeg()
    {
        // Een uitwedstrijd: de uitploeg is het eigen team. Pagina en document tonen dezelfde andere ploeg (#1582).
        var model = PlannerShareModelBuilder.VanVeldbezetting(
            new[] { new Bezetting("10:00", "Eigen 35+1", "Thuis 35+2 - Eigen 35+1", "Eigen 35+1", "veld 1", "regulier", Tegenstander: "Thuis 35+2") },
            Zaterdag, "ALLSTARS");

        model.Wedstrijden.Single().Tegenstander.Should().Be("Thuis 35+2");
    }

    [Fact]
    public void VanVeldbezetting_ZonderTegenstander_ValtTerugOpDeUitploeg()
        => PlannerShareModelBuilder.VanVeldbezetting(
                new[] { new Bezetting("10:00", "JO10-1", "w", "Gasten JO10-2", null, null) }, Zaterdag, "ALLSTARS")
            .Wedstrijden.Single().Tegenstander.Should().Be("Gasten JO10-2");

    [Fact]
    public void VanVeldbezetting_BehoudtDeAfwijkingEnToontHaarInHetDocument()
    {
        // Een eigen regel die Sportlink niet kent mag in het gedeelde document niet als gewone bezetting verschijnen.
        var model = PlannerShareModelBuilder.VanVeldbezetting(
            new[] { new Bezetting("10:00", "Eigen 35+1", "Eigen 35+1 - Gast 35+1", "Gast 35+1", "veld 1", null, NietInSportlink: true) },
            Zaterdag, "ALLSTARS");

        var regel = model.Wedstrijden.Single();
        regel.NietInSportlink.Should().BeTrue();
        regel.TeamWeergave.Should().Be("Eigen 35+1 (niet in Sportlink)");
        PlannerShareHtmlGenerator.Genereer(model).Should().Contain("Eigen 35+1 (niet in Sportlink)");
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

    [Theory]
    [InlineData("Tegenstander 1 - AllStars JO10 1", "AllStars JO10 1", "Tegenstander 1")]   // uitwedstrijd
    [InlineData("AllStars JO10 1 - Tegenstander 1", "AllStars JO10 1", "Tegenstander 1")]   // thuiswedstrijd
    [InlineData("AllStars JO10-1 - Gasten JO10-10", "JO10-1", "Gasten JO10-10")]            // teamnaam ook in tegenstander
    [InlineData("A - B", "JO10-1", "B")]                                                    // team onvindbaar: gedrag #1363
    public void TegenstanderUitWedstrijd_MetTeamnaam_KiestDeAndereKant(string wedstrijd, string team, string verwacht)
    {
        PlannerShareModelBuilder.TegenstanderUitWedstrijd(wedstrijd, team).Should().Be(verwacht);
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
