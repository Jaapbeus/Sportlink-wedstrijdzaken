using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

/// <summary>Tekstbouwstenen van de automatische antwoorden (gedeeld door beide tiers).</summary>
public class AntwoordTekstTests
{
    [Theory]
    [InlineData(2026, 10, 10, 6, "Goedemorgen")]   // 08:00 NL (zomertijd eindigt 25 oktober)
    [InlineData(2026, 10, 10, 11, "Goedemiddag")]  // 13:00 NL
    [InlineData(2026, 10, 10, 17, "Goedenavond")]  // 19:00 NL
    public void Aanhef_VolgtHetUurInNederland(int jaar, int maand, int dag, int utcUur, string verwacht)
        => AntwoordTekst.Aanhef(new DateTime(jaar, maand, dag, utcUur, 0, 0, DateTimeKind.Utc)).Should().Be(verwacht);

    [Theory]
    [InlineData("Jan de Vries", "Jan")]
    [InlineData("  Jan  ", "Jan")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Voornaam_IsHetEersteWoord(string? naam, string verwacht)
        => AntwoordTekst.Voornaam(naam).Should().Be(verwacht);

    [Fact]
    public void Datum_GeldigeDatumWordtNederlands()
        => AntwoordTekst.Datum("2026-10-10").Should().Be("zaterdag 10 oktober 2026");

    [Theory]
    [InlineData(null, "de opgegeven datum")]
    [InlineData("volgende week", "volgende week")]
    public void Datum_OnleesbareTekstBlijftBehoudenOfWordtGeneriek(string? invoer, string verwacht)
        => AntwoordTekst.Datum(invoer).Should().Be(verwacht);

    private static SlotToewijzing Slot(int veld, string type) => new() { VeldNummer = veld, VeldType = type };

    [Fact]
    public void FilterAlternatieven_ZonderDrieKunstgrasveldenBlijftAllesStaan()
    {
        var lijst = new List<SlotToewijzing> { Slot(1, "kunstgras"), Slot(2, "kunstgras"), Slot(5, "gras") };

        AntwoordTekst.FilterAlternatieven(lijst).Should().HaveCount(3);
    }

    [Fact]
    public void FilterAlternatieven_MetDrieKunstgrasveldenVerdwijntAlleenNatuurgras()
    {
        var lijst = new List<SlotToewijzing>
        {
            Slot(1, "kunstgras"), Slot(2, "kunstgras"), Slot(3, "kunstgras"), Slot(5, "gras"), Slot(6, "hybride")
        };

        AntwoordTekst.FilterAlternatieven(lijst).Select(s => s.VeldNummer).Should().Equal(1, 2, 3, 6);
    }
}
