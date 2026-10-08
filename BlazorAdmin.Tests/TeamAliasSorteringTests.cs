using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>Tests voor de sorteerregel van de Teamaliassen-tabel (#1549).</summary>
public class TeamAliasSorteringTests
{
    private static readonly DateTime Basis = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TeamAliasDto Item(string tekst, int aantal, int? dagen = null) =>
        new()
        {
            RuweTekst = tekst,
            AantalKeerGebruikt = aantal,
            MtaInserted = dagen.HasValue ? Basis.AddDays(dagen.Value) : null
        };

    private static string[] Namen(IEnumerable<TeamAliasDto> items) => items.Select(i => i.RuweTekst).ToArray();

    [Fact]
    public void NieuweSortering_HeeftGeenActieveKolom_EnLaatVolgordeOngemoeid()
    {
        var s = new TeamAliasSortering();
        var items = new[] { Item("a", 3), Item("b", 1), Item("c", 2) };

        s.Kolom.Should().Be(TeamAliasSorteerKolom.Geen);
        Namen(s.Sorteer(items)).Should().Equal("a", "b", "c");
        s.AriaSort(TeamAliasSorteerKolom.AantalKeerGebruikt).Should().Be("none");
        s.Indicator(TeamAliasSorteerKolom.Aangemaakt).Should().BeEmpty();
    }

    [Fact]
    public void EersteKlik_SorteertOplopend_TweedeKlikAflopend_DerdeWeerOplopend()
    {
        var s = new TeamAliasSortering();
        var items = new[] { Item("a", 3), Item("b", 1), Item("c", 2) };

        s.Klik(TeamAliasSorteerKolom.AantalKeerGebruikt);
        Namen(s.Sorteer(items)).Should().Equal("b", "c", "a");
        s.AriaSort(TeamAliasSorteerKolom.AantalKeerGebruikt).Should().Be("ascending");
        s.Indicator(TeamAliasSorteerKolom.AantalKeerGebruikt).Should().Be("▲");

        s.Klik(TeamAliasSorteerKolom.AantalKeerGebruikt);
        Namen(s.Sorteer(items)).Should().Equal("a", "c", "b");
        s.AriaSort(TeamAliasSorteerKolom.AantalKeerGebruikt).Should().Be("descending");
        s.Indicator(TeamAliasSorteerKolom.AantalKeerGebruikt).Should().Be("▼");

        s.Klik(TeamAliasSorteerKolom.AantalKeerGebruikt);
        s.Oplopend.Should().BeTrue();
    }

    [Fact]
    public void WisselNaarAndereKolom_BegintWeerOplopend_EnDeVorigeKolomWordtNone()
    {
        var s = new TeamAliasSortering();
        s.Klik(TeamAliasSorteerKolom.AantalKeerGebruikt);
        s.Klik(TeamAliasSorteerKolom.AantalKeerGebruikt); // aflopend

        s.Klik(TeamAliasSorteerKolom.Aangemaakt);

        s.Kolom.Should().Be(TeamAliasSorteerKolom.Aangemaakt);
        s.Oplopend.Should().BeTrue();
        s.AriaSort(TeamAliasSorteerKolom.AantalKeerGebruikt).Should().Be("none");
        s.AriaSort(TeamAliasSorteerKolom.Aangemaakt).Should().Be("ascending");
    }

    [Fact]
    public void KlikOpGeen_VeranderdNiets()
    {
        var s = new TeamAliasSortering();
        s.Klik(TeamAliasSorteerKolom.Aangemaakt);

        s.Klik(TeamAliasSorteerKolom.Geen);

        s.Kolom.Should().Be(TeamAliasSorteerKolom.Aangemaakt);
        s.Oplopend.Should().BeTrue();
    }

    [Fact]
    public void Aangemaakt_SorteertChronologisch_OplopendEnAflopend()
    {
        var s = new TeamAliasSortering();
        var items = new[] { Item("midden", 0, 5), Item("laat", 0, 9), Item("vroeg", 0, 1) };

        s.Klik(TeamAliasSorteerKolom.Aangemaakt);
        Namen(s.Sorteer(items)).Should().Equal("vroeg", "midden", "laat");

        s.Klik(TeamAliasSorteerKolom.Aangemaakt);
        Namen(s.Sorteer(items)).Should().Equal("laat", "midden", "vroeg");
    }

    [Fact]
    public void Aangemaakt_NullDatums_StaanInBeideRichtingenAchteraan_InOorspronkelijkeVolgorde()
    {
        var s = new TeamAliasSortering();
        var items = new[] { Item("leeg1", 0), Item("laat", 0, 9), Item("leeg2", 0), Item("vroeg", 0, 1) };

        s.Klik(TeamAliasSorteerKolom.Aangemaakt);
        Namen(s.Sorteer(items)).Should().Equal("vroeg", "laat", "leeg1", "leeg2");

        s.Klik(TeamAliasSorteerKolom.Aangemaakt);
        Namen(s.Sorteer(items)).Should().Equal("laat", "vroeg", "leeg1", "leeg2");
    }

    [Fact]
    public void GelijkeWaarden_BlijvenStabiel_InBeideRichtingen()
    {
        var s = new TeamAliasSortering();
        var items = new[] { Item("x", 2), Item("y", 1), Item("z", 2), Item("w", 1) };

        s.Klik(TeamAliasSorteerKolom.AantalKeerGebruikt);
        Namen(s.Sorteer(items)).Should().Equal("y", "w", "x", "z");

        s.Klik(TeamAliasSorteerKolom.AantalKeerGebruikt);
        Namen(s.Sorteer(items)).Should().Equal("x", "z", "y", "w");
    }

    [Fact]
    public void Sorteer_WijzigtDeBronlijstNiet_EnVerwerktEenLegeLijst()
    {
        var s = new TeamAliasSortering();
        s.Klik(TeamAliasSorteerKolom.AantalKeerGebruikt);
        var items = new List<TeamAliasDto> { Item("a", 3), Item("b", 1) };

        _ = s.Sorteer(items);

        Namen(items).Should().Equal("a", "b");
        s.Sorteer(new List<TeamAliasDto>()).Should().BeEmpty();
    }
}
