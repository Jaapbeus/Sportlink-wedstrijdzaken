using FluentAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

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

    [Fact]
    public void SqlExpr_BevatExplicieteSeniorenCases()
    {
        var expr = LeeftijdNormalisatie.SqlExpr("t.[leeftijdscategorie]");

        expr.Should().Contain("SENIOREN");
        expr.Should().Contain("'1-99'");
        expr.Should().Contain("'VR'");
    }
}
