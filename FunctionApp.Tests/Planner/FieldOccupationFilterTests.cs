using FluentAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Regressietests voor de herplan-exclusie (#574, #578).
///
/// De oude implementatie deed <c>Wedstrijd.Contains(code.ToString())</c> op de wedstrijdnaam.
/// Dat sloot de verkeerde wedstrijden uit (code 123 matcht ook 3123) en viel stilzwijgend om
/// zodra de opmaak van de wedstrijdnaam wijzigde. Nu wordt op de exacte wedstrijdcode gefilterd.
/// </summary>
public class FieldOccupationFilterTests
{
    private static BestaandeWedstrijd Wedstrijd(long? code, string naam, int veld = 1, string van = "10:00") =>
        new()
        {
            Datum = new DateOnly(2026, 9, 12),
            VeldNummer = veld,
            AanvangsTijd = TimeOnly.Parse(van),
            EindTijd = TimeOnly.Parse(van).AddMinutes(105),
            VeldDeelGebruik = 1.00m,
            Wedstrijd = naam,
            Wedstrijdcode = code,
            Bron = "Competitie"
        };

    [Fact]
    public void Filter_SluitAlleenDeExacteWedstrijdcodeUit()
    {
        var occs = new List<BestaandeWedstrijd>
        {
            Wedstrijd(123, "Team A - Team B", veld: 1, van: "10:00"),
            Wedstrijd(456, "Team C - Team D", veld: 2, van: "10:00")
        };

        var resultaat = PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(occs, 123);

        resultaat.Should().HaveCount(1);
        resultaat[0].Wedstrijdcode.Should().Be(456);
    }

    [Fact]
    public void Filter_DeelstringMatchWordtNietUitgesloten()
    {
        // Kern van #574: 123 is een deelstring van 3123 — mocht nooit uitgesloten worden
        var occs = new List<BestaandeWedstrijd>
        {
            Wedstrijd(3123, "Team A - Team B", veld: 1, van: "10:00"),
            Wedstrijd(1234, "Team C - Team D", veld: 2, van: "10:00"),
            Wedstrijd(123,  "Team E - Team F", veld: 3, van: "10:00")
        };

        var resultaat = PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(occs, 123);

        resultaat.Should().HaveCount(2);
        resultaat.Select(o => o.Wedstrijdcode).Should().BeEquivalentTo(new long?[] { 3123, 1234 });
    }

    [Fact]
    public void Filter_WedstrijdcodeInNaamMaarAndereCode_BlijftStaan()
    {
        // De code komt in de tekst voor maar hoort bij een andere wedstrijd —
        // met de oude Contains-implementatie viel deze wedstrijd onterecht weg.
        var occs = new List<BestaandeWedstrijd>
        {
            Wedstrijd(999, "Team 123 - Team B")
        };

        PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(occs, 123)
            .Should().HaveCount(1);
    }

    [Fact]
    public void Filter_RijenZonderWedstrijdcode_BlijvenStaan()
    {
        // Planner-slots zonder Sportlink-tegenhanger hebben geen code; die bezetting
        // moet meegenomen blijven, anders wordt de bezetting onderschat.
        var occs = new List<BestaandeWedstrijd>
        {
            Wedstrijd(null, "TEST JO14-1 - Oefen tegenstander"),
            Wedstrijd(123,  "Team A - Team B", veld: 2)
        };

        var resultaat = PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(occs, 123);

        resultaat.Should().HaveCount(1);
        resultaat[0].Wedstrijdcode.Should().BeNull();
    }

    [Fact]
    public void Filter_WedstrijdZonderNaam_VeroorzaaktGeenFout()
    {
        var occs = new List<BestaandeWedstrijd> { Wedstrijd(123, null!) };

        PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(occs, 123)
            .Should().BeEmpty();
    }

    [Fact]
    public void Filter_LegeBezetting_GeeftLegeLijst()
    {
        PlannerAvailabilityRepository.FilterExcludingWedstrijdcode(new List<BestaandeWedstrijd>(), 123)
            .Should().BeEmpty();
    }
}
