using AwesomeAssertions;
using Planner.Shared.Avg;
using Xunit;

namespace Planner.Shared.Tests.Avg;

/// <summary>
/// Welke <c>wedstrijddatum</c>-waarden de his-retentie als datum telt (#1698, Codex-review ronde 2).
/// Beide tiers lezen alleen de eerste tien tekens en laten de keuze aan
/// <see cref="HisPersoonsgegevensRetentie.SelecteerTeWissenDatumdelen"/>; de gedeelde tabel
/// <see cref="HisWedstrijddatumGevallen"/> draait ook in beide database-integratiesuites.
/// </summary>
public class HisWedstrijddatumSelectieTests
{
    public static TheoryData<string?, bool, string> Gevallen()
    {
        var data = new TheoryData<string?, bool, string>();
        foreach (var (waarde, wissen, reden) in HisWedstrijddatumGevallen.Alle)
            data.Add(waarde, wissen, reden);
        return data;
    }

    [Theory]
    [MemberData(nameof(Gevallen))]
    public void SelecteerTeWissenDatumdelen_VolgtDeGedeeldeTabel(string? waarde, bool wissen, string reden)
    {
        var deel = EersteTienTekens(waarde);

        var gekozen = HisPersoonsgegevensRetentie.SelecteerTeWissenDatumdelen([deel], HisWedstrijddatumGevallen.Grens);

        if (wissen)
            gekozen.Should().Equal([deel], reden);
        else
            gekozen.Should().BeEmpty(reden);
    }

    [Fact]
    public void SelecteerTeWissenDatumdelen_HeleTabel_UniekEnOrdinaalGesorteerd()
    {
        var delen = HisWedstrijddatumGevallen.Alle.Select(g => EersteTienTekens(g.Waarde)).ToList();
        var verwacht = HisWedstrijddatumGevallen.Alle.Where(g => g.Wissen)
            .Select(g => EersteTienTekens(g.Waarde)!).Distinct().Order(StringComparer.Ordinal).ToList();

        HisPersoonsgegevensRetentie.SelecteerTeWissenDatumdelen(delen.Concat(delen), HisWedstrijddatumGevallen.Grens)
            .Should().Equal(verwacht);
    }

    [Theory]
    [InlineData("2024-02-29", 2024, 2, 29)]
    [InlineData("0001-01-01", 1, 1, 1)]
    [InlineData("9999-12-31", 9999, 12, 31)]
    public void IsGeldigDatumdeel_GeeftDeDatumTerug(string deel, int jaar, int maand, int dag)
    {
        HisPersoonsgegevensRetentie.IsGeldigDatumdeel(deel, out var datum).Should().BeTrue();
        datum.Should().Be(new DateOnly(jaar, maand, dag));
    }

    [Theory]
    [InlineData("2026-06-30T")]
    [InlineData("2026-06-30 ")]
    [InlineData("2026-06-3")]
    public void IsGeldigDatumdeel_EistPreciesTienTekens(string deel)
        => HisPersoonsgegevensRetentie.IsGeldigDatumdeel(deel, out _).Should().BeFalse(
            "de tier levert de eerste tien tekens; meer of minder betekent dat de tier iets anders deed");

    /// <summary>Wat <c>left(x, 10)</c>/<c>LEFT(x, 10)</c> op de tier oplevert voor tekst in het BMP.</summary>
    private static string? EersteTienTekens(string? waarde)
        => waarde is null || waarde.Length <= 10 ? waarde : waarde[..10];
}
