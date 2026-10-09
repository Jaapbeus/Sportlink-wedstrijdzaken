using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// #1587: de ene vertaling van een dagdeel naar een tijdvenster, en de zinnen waarmee een antwoord
/// aangeeft welk dagdeel is gecontroleerd. Beide tiers delegeren hierheen.
/// </summary>
public class DagdeelVenstersTests
{
    [Theory]
    [InlineData("ochtend", "08:30", "12:00")]
    [InlineData("middag", "12:00", "17:00")]
    [InlineData("avond", "17:00", "22:00")]
    public void ZoekVenster_GeeftDeGrenzenVanHetDagdeel(string dagdeel, string van, string tot)
    {
        var venster = DagdeelVenster.ZoekVenster(dagdeel);

        venster.Should().NotBeNull();
        venster!.Value.Van.Should().Be(TimeOnly.Parse(van));
        venster.Value.Tot.Should().Be(TimeOnly.Parse(tot));
    }

    [Theory]
    [InlineData("OCHTEND ", "ochtend")]
    [InlineData(" Middag", "middag")]
    [InlineData("avond", "avond")]
    [InlineData("nacht", null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Normaliseer_AccepteertAlleenDeDrieBekendeDagdelen(string? invoer, string? verwacht)
        => DagdeelVenster.Normaliseer(invoer).Should().Be(verwacht);

    [Fact]
    public void Bepaal_BijOnbekendDagdeel_GebruiktDeStandaardwaarden()
    {
        var standaardVan = new TimeOnly(8, 30);
        var standaardTot = new TimeOnly(22, 0);

        DagdeelVenster.Bepaal(null, standaardVan, standaardTot).Should().Be((standaardVan, standaardTot));
        DagdeelVenster.Bepaal("nacht", standaardVan, standaardTot).Should().Be((standaardVan, standaardTot));
        DagdeelVenster.Bepaal("middag", standaardVan, standaardTot)
            .Should().Be((new TimeOnly(12, 0), new TimeOnly(17, 0)));
    }

    [Fact]
    public void Omschrijving_NoemtHetDagdeelEnHetGecontroleerdeVenster()
    {
        DagdeelVenster.Omschrijving("ochtend").Should().Be("de ochtend (08:30 - 12:00)");
        DagdeelVenster.Omschrijving("middag").Should().Be("de middag (12:00 - 17:00)");
        DagdeelVenster.Omschrijving(null).Should().BeNull();
    }

    [Fact]
    public void ControleZin_ZegtDatAndereDagdelenNietZijnGecontroleerd()
    {
        var zin = DagdeelVenster.ControleZin("ochtend");

        zin.Should().Contain("alleen de ochtend (08:30 - 12:00) gecontroleerd");
        zin.Should().Contain("andere dagdelen zijn niet meegenomen");
        DagdeelVenster.ControleZin(null).Should().BeEmpty();
    }

    [Fact]
    public void GeenRuimteDeelzin_BenoemtHetDagdeelEnZwijgtOverAndereDagdelen()
    {
        var zin = DagdeelVenster.GeenRuimteDeelzin("middag");

        zin.Should().Be("in de middag (12:00 - 17:00) is helaas niets beschikbaar");
        zin.Should().NotContain("ochtend").And.NotContain("avond");
        DagdeelVenster.GeenRuimteDeelzin(null).Should().BeEmpty();
    }

    [Fact]
    public void VoegControleZinToe_VoegtDeZinAlleenToeBijEenDagdeel()
    {
        DagdeelVenster.VoegControleZinToe("tekst", null).Should().Be("tekst");
        DagdeelVenster.VoegControleZinToe("tekst", "nacht").Should().Be("tekst");
        DagdeelVenster.VoegControleZinToe("tekst", "avond")
            .Should().Be("tekst\n\n" + DagdeelVenster.ControleZin("avond"));
    }

    [Fact]
    public void GeenVeldZin_ZonderDagdeel_BehoudtDeDagbredeZinEnReden()
        => DagdeelVenster.GeenVeldZin("zaterdag 10 oktober", null, "Geen wedstrijden mogelijk.")
            .Should().Be("Op zaterdag 10 oktober is helaas geen veld beschikbaar. Geen wedstrijden mogelijk.");

    [Fact]
    public void GeenVeldZin_MetDagdeel_NoemtHetDagdeelEnLaatDeDagbredeRedenWeg()
        => DagdeelVenster.GeenVeldZin("zaterdag 10 oktober", "ochtend", "Geen beschikbare vensters op zaterdag 10 oktober.")
            .Should().Be("Op zaterdag 10 oktober in de ochtend (08:30 - 12:00) is helaas niets beschikbaar.");

    [Fact]
    public void GeenVeldRegel_GeeftEenDatumkopMetOfZonderDagdeel()
    {
        DagdeelVenster.GeenVeldRegel("za 10 oktober", "middag", "reden")
            .Should().Be("**za 10 oktober:** In de middag (12:00 - 17:00) is helaas niets beschikbaar.\n");
        DagdeelVenster.GeenVeldRegel("za 10 oktober", null, "reden")
            .Should().Be("**za 10 oktober:** Helaas geen veld beschikbaar. reden\n");
        DagdeelVenster.GeenVeldRegel("za 10 oktober", null, null)
            .Should().Be("**za 10 oktober:** Helaas geen veld beschikbaar.\n");
    }

    [Fact]
    public void ControleAlinea_PaktHetEersteDagdeelEnIsLeegZonder()
    {
        DagdeelVenster.ControleAlinea(new string?[] { null, "ochtend", "ochtend" })
            .Should().Be(DagdeelVenster.ControleZin("ochtend") + "\n\n");
        DagdeelVenster.ControleAlinea(new string?[] { null, "" }).Should().BeEmpty();
    }
}
