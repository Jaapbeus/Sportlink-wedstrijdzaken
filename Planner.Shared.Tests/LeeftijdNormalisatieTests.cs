using FluentAssertions;
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

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normaliseer_LegeInvoer_LevertLegeSleutel(string? invoer, string verwacht)
    {
        LeeftijdNormalisatie.Normaliseer(invoer).Should().Be(verwacht);
    }
}
