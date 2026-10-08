using AwesomeAssertions;
using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Xunit;

namespace BlazorAdmin.Tests;

/// <summary>#1583: filters van de pagina E-maillog en opmaak van het tester-eindoordeel.</summary>
public class EmailLogFilterTests
{
    private static readonly DateTime Vandaag = new(2026, 10, 7, 14, 30, 0);

    [Fact]
    public void Vanaf_24Uur_IsGisteren() => EmailLogFilter.Vanaf("24u", Vandaag).Should().Be(new DateTime(2026, 10, 6));

    [Fact]
    public void Vanaf_7Dagen_Is7DagenTerug() => EmailLogFilter.Vanaf("7d", Vandaag).Should().Be(new DateTime(2026, 9, 30));

    [Fact]
    public void Vanaf_30Dagen_Is30DagenTerug() => EmailLogFilter.Vanaf("30d", Vandaag).Should().Be(new DateTime(2026, 9, 7));

    [Theory]
    [InlineData("alles")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("onzin")]
    public void Vanaf_AllesOfOnbekend_GeenOndergrens(string? periode)
        => EmailLogFilter.Vanaf(periode, Vandaag).Should().BeNull();

    [Theory]
    [InlineData("Review", "Review")]
    [InlineData("Fout", "Fout")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Onbekend'; DROP", null)]
    public void StatusParameter_AlleenBekendeStatussen(string? invoer, string? verwacht)
        => EmailLogFilter.StatusParameter(invoer).Should().Be(verwacht);

    [Fact]
    public void Statussen_BevattDeStatussenVanInstellingen()
        => EmailLogFilter.Statussen.Select(s => s.Waarde).Should()
            .Contain(new[] { "Review", "AntwoordVerstuurd", "GeenAntwoordNodig", "Fout", "BuitenScope" });

    [Theory]
    [InlineData("Review", false, "alert alert-warning")]
    [InlineData("AutomatischVerstuurd", false, "alert alert-success")]
    [InlineData("AutomatischVerstuurd", true, "alert alert-danger")]
    [InlineData("GeenAntwoord", false, "alert alert-secondary")]
    public void AlertKlasse_VolgtUitkomstEnWaarschuwing(string uitkomst, bool waarschuwing, string verwacht)
        => TesterEindoordeelWeergave.AlertKlasse(new TesterEindoordeelDto { Uitkomst = uitkomst, Waarschuwing = waarschuwing })
            .Should().Be(verwacht);

    [Fact]
    public void AlertKlasse_ZonderEindoordeel_IsNeutraal()
        => TesterEindoordeelWeergave.AlertKlasse(null).Should().Be("alert alert-secondary");

    [Fact]
    public void VoorbeeldKop_GebruiktHetLabelVanDeServer_EnValtTerugBijEenOudereServer()
    {
        TesterEindoordeelWeergave.VoorbeeldKop(new TesterEindoordeelDto { ConceptLabel = "Concept-antwoord" }).Should().Be("Concept-antwoord");
        TesterEindoordeelWeergave.VoorbeeldKop(null).Should().Contain("Voorbeeld-antwoord");
    }
}
