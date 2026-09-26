using BlazorAdmin.Models;
using BlazorAdmin.Services;
using AwesomeAssertions;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Regressietests voor #1331: de wedstrijdenlijst onder de Dagplanning-Gantt had geen eigen
/// sortering, waardoor de zichtbare volgorde afhing van de volgorde in de AutoPlan-API-respons.
/// Dekt tijdsortering, gelijke tijden met zowel numerieke als alfabetische veldnamen (A/B/C moeten
/// evengoed werken als "Veld 1"/"Veld 2"), en ontbrekende/ongeldige gegevens.
/// </summary>
public class DagplanningSorteringTests
{
    private static AutoPlanWedstrijdItemDto Item(
        string wedstrijd, string? tijd, int? veldNummer, string? veldNaam = null) =>
        new()
        {
            Wedstrijd = wedstrijd,
            OptimaalTijd = tijd,
            OptimaalVeldNummer = veldNummer,
            OptimaalVeldNaam = veldNaam,
        };

    [Fact]
    public void Sorteer_VerschillendeTijden_StaanChronologischVroegNaarLaatOngeachtInvoervolgorde()
    {
        var items = new[]
        {
            Item("Laat", "14:00", 1),
            Item("Vroeg", "09:00", 1),
            Item("Midden", "11:30", 1),
        };

        var gesorteerd = DagplanningSortering.Sorteer(items).ToList();

        gesorteerd.Select(i => i.Wedstrijd).Should().Equal("Vroeg", "Midden", "Laat");
    }

    [Fact]
    public void Sorteer_GelijkeTijdMetNumeriekeVeldnamen_SorteertOpVeldnummer()
    {
        var items = new[]
        {
            Item("Wedstrijd op veld 3", "10:00", 3, "Veld 3"),
            Item("Wedstrijd op veld 1", "10:00", 1, "Veld 1"),
            Item("Wedstrijd op veld 2", "10:00", 2, "Veld 2"),
        };

        var gesorteerd = DagplanningSortering.Sorteer(items).ToList();

        gesorteerd.Select(i => i.OptimaalVeldNummer).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Sorteer_GelijkeTijdMetAlfabetischeVeldnamen_SorteertOpDeInterneClubveldvolgordeNietOpDeLetter()
    {
        // Een club met velden A, B, C mag hetzelfde gedrag krijgen als een club met "Veld 1/2/3":
        // de configureerde volgorde (OptimaalVeldNummer) is leidend, niet de letter zelf. Hier
        // wijkt de veldvolgorde bewust af van het alfabet (C heeft veldnummer 1) om dat aan te tonen.
        var items = new[]
        {
            Item("Wedstrijd op veld B", "10:00", 3, "B"),
            Item("Wedstrijd op veld C", "10:00", 1, "C"),
            Item("Wedstrijd op veld A", "10:00", 2, "A"),
        };

        var gesorteerd = DagplanningSortering.Sorteer(items).ToList();

        gesorteerd.Select(i => i.OptimaalVeldNaam).Should().Equal(
            new[] { "C", "A", "B" },
            "de volgorde volgt OptimaalVeldNummer (1,2,3) — niet de alfabetische letter zelf");
    }

    [Fact]
    public void Sorteer_OntbrekendeOfOngeldigeTijd_StaatAltijdAchteraanNooitOnverwachtBovenaan()
    {
        var items = new[]
        {
            Item("Geen tijd", null, 1),
            Item("Ongeldige tijd", "niet-een-tijd", 1),
            Item("Wel een tijd", "08:00", 1),
        };

        var gesorteerd = DagplanningSortering.Sorteer(items).ToList();

        gesorteerd[0].Wedstrijd.Should().Be("Wel een tijd");
        gesorteerd.Skip(1).Select(i => i.Wedstrijd).Should()
            .BeEquivalentTo(new[] { "Geen tijd", "Ongeldige tijd" });
    }

    [Fact]
    public void Sorteer_OntbrekendVeldnummerBijGelijkeTijd_StaatAchterAlleWedstrijdenMetEenVeldnummer()
    {
        var items = new[]
        {
            Item("Zonder veld", "10:00", null),
            Item("Veld 2", "10:00", 2),
            Item("Veld 1", "10:00", 1),
        };

        var gesorteerd = DagplanningSortering.Sorteer(items).ToList();

        gesorteerd.Select(i => i.Wedstrijd).Should().Equal("Veld 1", "Veld 2", "Zonder veld");
    }

    [Fact]
    public void Sorteer_LegeLijst_GeeftLegeLijstZonderException()
    {
        var gesorteerd = DagplanningSortering.Sorteer(Enumerable.Empty<AutoPlanWedstrijdItemDto>());

        gesorteerd.Should().BeEmpty();
    }
}
