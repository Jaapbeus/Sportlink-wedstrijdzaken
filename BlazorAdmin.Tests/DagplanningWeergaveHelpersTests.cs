using BlazorAdmin.Services;
using AwesomeAssertions;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>
/// Unit tests voor de pure Gantt-weergavehelpers die bij #1361 zijn losgetrokken uit het toenmalige
/// <c>Dagplanning.razor.cs</c> naar <see cref="DagplanningWeergaveHelpers"/>, zodat zowel
/// <c>Planning.razor.cs</c> als <c>VeldOptimalisatie.razor.cs</c> dezelfde implementatie gebruiken.
/// De logica zelf is ongewijzigd verplaatst — deze tests documenteren het bestaande gedrag.
/// </summary>
public class DagplanningWeergaveHelpersTests
{
    [Theory]
    [InlineData("Kunstgras 1 A", "Kunstgras 1", "A")]
    [InlineData("Kunstgras 1 A2", "Kunstgras 1", "A2")]
    [InlineData("Kunstgras 1 B1", "Kunstgras 1", "B1")]
    // #665: een gewoon veldnummer mag nooit als subpositie worden gelezen — anders vallen alle
    // velden samen op één tijdlijnrij.
    [InlineData("Kunstgras 1", "Kunstgras 1", null)]
    [InlineData("Gras 3", "Gras 3", null)]
    public void GanttSplitVeld_SplitstAlleenEchteSubposities(string veld, string verwachteBasis, string? verwachteSub)
    {
        var (basis, sub) = DagplanningWeergaveHelpers.GanttSplitVeld(veld);

        basis.Should().Be(verwachteBasis);
        sub.Should().Be(verwachteSub);
    }

    [Fact]
    public void GanttExtractSubPos_LegeInvoerGeeftNull()
    {
        DagplanningWeergaveHelpers.GanttExtractSubPos(null).Should().BeNull();
        DagplanningWeergaveHelpers.GanttExtractSubPos("  ").Should().BeNull();
    }

    [Theory]
    [InlineData(25, 100, "25.00")]
    [InlineData(1, 3, "33.33")]
    [InlineData(5, 0, "0.00")]
    public void GanttPct_ZetVerhoudingOmNaarCultuurinvariantePercentagestring(int deel, int totaal, string verwacht)
    {
        DagplanningWeergaveHelpers.GanttPct(deel, totaal).Should().Be(verwacht);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(90, "01:30")]
    // #1107: de as loopt tot 1440 minuten (24:00) voor een wedstrijd die ná 23:00 eindigt — dat ligt
    // buiten het bereik van TimeOnly, dus dit blijft bewust een losse minutenberekening.
    [InlineData(1440, "24:00")]
    public void GanttUurLabel_FormatteertMinutenAlsUurMinuut(int minuut, string verwacht)
    {
        DagplanningWeergaveHelpers.GanttUurLabel(minuut).Should().Be(verwacht);
    }

    [Fact]
    public void GanttLabelTransform_EersteUurLijntLinksUit()
    {
        DagplanningWeergaveHelpers.GanttLabelTransform(uur: 0, startMinuut: 0, eindMinuut: 600)
            .Should().Be("translateX(0)");
    }

    [Fact]
    public void GanttLabelTransform_LaatsteUurLijntRechtsUit()
    {
        DagplanningWeergaveHelpers.GanttLabelTransform(uur: 600, startMinuut: 0, eindMinuut: 600)
            .Should().Be("translateX(-100%)");
    }

    [Fact]
    public void GanttLabelTransform_TussenliggendUurBlijftGecentreerd()
    {
        DagplanningWeergaveHelpers.GanttLabelTransform(uur: 300, startMinuut: 0, eindMinuut: 600)
            .Should().Be("translateX(-50%)");
    }

    [Fact]
    public void GanttMatchLabel_GebruiktWedstrijdnaamAlsBeschikbaar()
    {
        DagplanningWeergaveHelpers.GanttMatchLabel("AllStars JO10-1 - FC Onbekend JO10-2", "AllStars JO10-1")
            .Should().Be("AllStars JO10-1 - FC Onbekend JO10-2");
    }

    [Fact]
    public void GanttMatchLabel_ValtTerugOpTeamnaamZonderWedstrijdnaam()
    {
        DagplanningWeergaveHelpers.GanttMatchLabel(null, "AllStars JO10-1").Should().Be("AllStars JO10-1");
    }

    [Fact]
    public void GanttLabel_KortLabelBlijftOngewijzigd()
    {
        DagplanningWeergaveHelpers.GanttLabel("AllStars - FC Onbekend").Should().Be("AllStars - FC Onbekend");
    }

    [Fact]
    public void GanttLabel_LangLabelStriptHetAchtervoegselNaHetKoppelteken()
    {
        var label = "AllStars FC eerste elftal senioren - FC Onbekend JO7 1";

        DagplanningWeergaveHelpers.GanttLabel(label).Should().Be("AllStars FC eerste elftal senioren");
    }

    [Fact]
    public void VolgendeZaterdag_ValtAltijdOpEenZaterdagInDeToekomst()
    {
        var resultaat = DagplanningWeergaveHelpers.VolgendeZaterdag();

        resultaat.DayOfWeek.Should().Be(DayOfWeek.Saturday);
        resultaat.Should().BeAfter(DateOnly.FromDateTime(DateTime.Today));
    }

    // #1388: GanttKleur/GanttVoorkeurBalkKleur gaven vóór deze wijziging letterlijke hex terug
    // (bijv. "#0d6efd"). Ze geven nu een CSS var()-verwijzing naar een instelbare paletsleutel uit
    // ThemePresets.Kleuren terug — deze tests documenteren dat contract en bewaken dat de sleutel
    // in de var()-naam blijft overeenkomen met de kebab-case vorm die theme.js ervan maakt.
    [Theory]
    [InlineData("wijziging",   "var(--theme-status-wijziging)")]
    [InlineData("nieuw-slot",  "var(--theme-status-nieuw-slot)")]
    [InlineData("ongewijzigd", "var(--theme-status-ongewijzigd)")]
    [InlineData("onbekend-team", "var(--theme-secondary)")]
    public void GanttKleur_GeeftVarVerwijzingNaarPaletsleutelTerug(string status, string verwacht)
    {
        DagplanningWeergaveHelpers.GanttKleur(status).Should().Be(verwacht);
    }

    [Fact]
    public void GanttVoorkeurBalkKleur_GeenVoorkeurGeeftLegeString()
    {
        DagplanningWeergaveHelpers.GanttVoorkeurBalkKleur(afwijking: null, voorkeurTijd: null, kleineAfwijkingDrempelMinuten: 15)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(0,  15, "var(--theme-voorkeur-op-tijd)")]
    [InlineData(10, 15, "var(--theme-voorkeur-kleine-afwijking)")]
    [InlineData(-10, 15, "var(--theme-voorkeur-kleine-afwijking)")]
    [InlineData(20, 15, "var(--theme-voorkeur-grote-afwijking)")]
    public void GanttVoorkeurBalkKleur_KiestPaletsleutelOpAbsoluteAfwijking(int afwijking, int drempel, string verwacht)
    {
        DagplanningWeergaveHelpers.GanttVoorkeurBalkKleur(afwijking, voorkeurTijd: "10:00", kleineAfwijkingDrempelMinuten: drempel)
            .Should().Be(verwacht);
    }
}
