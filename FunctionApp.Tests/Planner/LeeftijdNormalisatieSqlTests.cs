using AwesomeAssertions;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// Alleen de SQL Server-SQL-generatie wordt hier getoetst. De pure C#-normalisatie is naar
/// <c>Planner.Shared.LeeftijdNormalisatie</c> verhuisd (#889) en wordt daar getest, samen met de
/// invariant dat beide SQL-varianten hetzelfde resultaat horen te geven.
/// </summary>
public class LeeftijdNormalisatieSqlTests
{
    [Fact]
    public void SqlExpr_BevatExplicieteSeniorenCases()
    {
        var expr = LeeftijdNormalisatieSql.SqlExpr("t.[leeftijdscategorie]");

        expr.Should().Contain("SENIOREN");
        expr.Should().Contain("'1-99'");
        expr.Should().Contain("'VR'");
    }

    /// <summary>
    /// #1332: de vorige expressie herkende alleen "Meiden" als sufix (<c>LIKE '%Meiden'</c>) en
    /// stripte vervolgens blind de substrings "JO"/"MO"/" Meiden" — een aanname die faalt zodra het
    /// cijfer niet direct na "JO"/"MO" staat, zoals bij Sportlinks volwoord-variant
    /// "Onder 13 Meiden". Deze test kan geen live SQL Server draaien (dat testproject heeft daar
    /// geen containeropzet voor, in tegenstelling tot de Postgres-tier — zie
    /// <c>PostgresLeeftijdNormalisatieSqlIntegrationTests</c> voor de daadwerkelijk uitgevoerde
    /// tegenhanger) en toetst daarom de structuur van de gegenereerde expressie: cijfer-extractie
    /// via <c>TRANSLATE</c>/<c>REPLICATE</c> i.p.v. de oude blinde substring-strip, en "Meiden"
    /// herkend ongeacht positie in de string (<c>%Meiden%</c>, niet alleen als suffix).
    /// </summary>
    [Fact]
    public void SqlExpr_GebruiktCijferExtractieInPlaatsVanBlindeSubstringStrip()
    {
        var expr = LeeftijdNormalisatieSql.SqlExpr("t.[leeftijdscategorie]");

        expr.Should().Contain("TRANSLATE");
        expr.Should().Contain("REPLICATE");
        expr.Should().Contain("%Meiden%");
        expr.Should().Contain("%Meisjes%");
        expr.Should().NotContain("LIKE '%Meiden'\n");
    }
}
