using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Planner.Shared;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// De SQL Server-SQL-generatie wordt structureel en met een wegwerpdatabase getoetst. De pure
/// C#-normalisatie is naar <c>Planner.Shared.LeeftijdNormalisatie</c> verhuisd (#889).
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
        expr.Should().Contain("LIKE 'MO%'");
    }

    /// <summary>
    /// #1332: de vorige expressie herkende alleen "Meiden" als sufix (<c>LIKE '%Meiden'</c>) en
    /// stripte vervolgens blind de substrings "JO"/"MO"/" Meiden" — een aanname die faalt zodra het
    /// cijfer niet direct na "JO"/"MO" staat, zoals bij Sportlinks volwoord-variant
    /// "Onder 13 Meiden". Deze test toetst de structuur van de gegenereerde expressie: cijfer-extractie
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

/// <summary>Voert de gegenereerde SQL Server-expressie uit tegen de verse CI-database.</summary>
public sealed class SqlServerLeeftijdNormalisatieSqlIntegrationTests
{
    [SqlServerFact]
    public async Task SqlExpr_GeeftCSharpUitkomstVoorExplicieteMoPrefixen()
    {
        var cases = new (string Invoer, string Verwacht)[]
        {
            ("MO13", "MO13"),
            ("MO 13", "MO13"),
            ("MOJO13", "MO13"),
            ("JO13", "JO13"),
            ("Onder 13 Meiden", "MO13")
        };

        await using var connection = new SqlConnection(
            Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar));
        await connection.OpenAsync();

        foreach (var (invoer, verwacht) in cases)
        {
            await using var command = new SqlCommand(
                $"SELECT {LeeftijdNormalisatieSql.SqlExpr("@invoer")}", connection);
            command.Parameters.AddWithValue("@invoer", invoer);
            var werkelijk = (string?)await command.ExecuteScalarAsync();

            Assert.Equal(verwacht, LeeftijdNormalisatie.Normaliseer(invoer));
            Assert.Equal(verwacht, werkelijk);
        }
    }
}
