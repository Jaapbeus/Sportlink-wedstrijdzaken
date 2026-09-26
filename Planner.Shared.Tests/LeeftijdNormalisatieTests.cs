using AwesomeAssertions;
using Planner.Shared;
using Xunit;

namespace Planner.Shared.Tests;

/// <summary>
/// Verhuisd uit <c>FunctionApp.Tests/Planner/LeeftijdNormalisatieTests.cs</c> (#889), samen met de
/// methode zelf. De SQL-generatie per tier heeft een eigen test in de betreffende boom.
/// </summary>
public class LeeftijdNormalisatieTests
{
    [Fact]
    public void Normaliseer_Senioren_MaptNaarEenTotNegenennegentig()
    {
        LeeftijdNormalisatie.Normaliseer("Senioren").Should().Be("1-99");
    }

    [Fact]
    public void Normaliseer_SeniorenVrouwen_MaptNaarVr()
    {
        LeeftijdNormalisatie.Normaliseer("Senioren Vrouwen").Should().Be("VR");
    }

    [Fact]
    public void Normaliseer_BestaandeJeugdFormaten_BlijvenOngewijzigd()
    {
        LeeftijdNormalisatie.Normaliseer("JO15 Meiden").Should().Be("MO15");
        LeeftijdNormalisatie.Normaliseer("Onder 13").Should().Be("JO13");
    }

    /// <summary>
    /// #1332: Sportlink levert voor sommige meisjesteams "Onder {n} Meiden" i.p.v. "{JO|MO}{n} Meiden".
    /// De vorige implementatie nam aan dat het cijfer al direct na "JO"/"MO" stond en leverde voor dit
    /// format de niet-bestaande sleutel "MOOnder 13" op, waardoor de speeltijd-lookup faalde en de
    /// wedstrijd zonder foutmelding uit de Dagplanning-Gantt verdween.
    /// </summary>
    [Theory]
    [InlineData("Onder 13 Meiden", "MO13")]
    [InlineData("Onder 11 Meiden", "MO11")]
    [InlineData("Onder 15 Meiden", "MO15")]
    [InlineData("Onder 17 Meiden", "MO17")]
    [InlineData("Onder 9 Meiden", "MO9")]
    public void Normaliseer_VolwoordVariantMetMeidenErachter_MaptNaarMoN(string invoer, string verwacht)
    {
        LeeftijdNormalisatie.Normaliseer(invoer).Should().Be(verwacht);
    }

    [Fact]
    public void Normaliseer_MeisjesOnderN_MaptNaarMoN()
    {
        // Voorheen leverde de Replace-keten voor dit format "MOJO15" op i.p.v. "MO15": de eerste
        // Replace("Onder ", "JO") gaf al "Meisjes JO15" vóór de tweede Replace("Meisjes ", "MO") kon
        // toeslaan.
        LeeftijdNormalisatie.Normaliseer("Meisjes Onder 15").Should().Be("MO15");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normaliseer_LegeInvoer_LevertLegeSleutel(string? invoer, string verwacht)
    {
        LeeftijdNormalisatie.Normaliseer(invoer).Should().Be(verwacht);
    }
}
