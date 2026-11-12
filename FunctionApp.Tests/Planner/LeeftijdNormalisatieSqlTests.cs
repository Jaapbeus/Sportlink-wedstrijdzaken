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

    /// <summary>#1863: O23-team → JO23-sleutel, met terugval op de gewone normalisatie als de JO23-rij ontbreekt of duur 0 heeft.</summary>
    [Fact]
    public void SpeeltijdSleutelSqlExpr_GeeftO23DeJo23SleutelMetTerugval()
    {
        var expr = LeeftijdNormalisatieSql.SpeeltijdSleutelSqlExpr("t.[leeftijdscategorie]", "m.[teamnaam]", "@clubCode");

        expr.Should().Contain("LIKE '%[^A-Z0-9]O23[^0-9]%'");
        expr.Should().Contain("LIKE '%[^A-Z0-9]JO23[^0-9]%'");
        expr.Should().Contain("m.[teamnaam]");
        expr.Should().Contain("jo23.[WedstrijdTotaal] > 0");
        expr.Should().Contain("jo23.[ClubCode] = @clubCode");
        expr.Should().Contain("THEN 'JO23'");
        expr.Should().Contain(LeeftijdNormalisatieSql.SqlExpr("t.[leeftijdscategorie]"), "buiten O23 blijft de gewone normalisatie gelden");
    }

    [Fact]
    public void PlannerMatchRepository_GebruiktDeTeamnaamBewareSleutelInAlleSpeeltijdenJoins()
    {
        var bron = File.ReadAllText(Path.Combine(RepoRoot(), "FunctionApp/Planner/Repositories/PlannerMatchRepository.cs"));
        bron.Should().NotContain("LeeftijdNormalisatieSql.SqlExpr(\"t.[leeftijdscategorie]\")",
            "een join op Speeltijden hoort de teamnaam te kennen (#1863)");
        bron.Should().Contain("SpeeltijdSleutelSqlExpr");
    }

    [Fact]
    public void PlannerView_KentDeO23Regel_InBeideBronnen()
    {
        // #1863: de view en het (enige uitgerolde) PostDeployment-script moeten dezelfde O23-regel hebben.
        var view = File.ReadAllText(Path.Combine(RepoRoot(), "Database/planner/Views/AlleWedstrijdenOpVeld.sql"));
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "Database/Script.PostDeployment1.sql"));

        view.Should().Contain("LIKE '%[^A-Z0-9]O23[^0-9]%'").And.Contain("jo23.[WedstrijdTotaal] > 0").And.Contain("THEN 'JO23'");
        script.Should().Contain("LIKE ''%[^A-Z0-9]O23[^0-9]%''").And.Contain("jo23.[WedstrijdTotaal] > 0").And.Contain("THEN ''JO23''");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo-root niet gevonden.");
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

    /// <summary>
    /// #1863: voert de teamnaam-bewuste expressie echt uit en toetst de uitkomst tegen
    /// <see cref="LeeftijdNormalisatie.SpeeltijdSleutel"/> (C#-tegenhanger). De clubcode is een eigen,
    /// wegwerp-waarde zodat de test geen bestaande Speeltijden-rijen raakt.
    /// </summary>
    [SqlServerFact]
    public async Task SpeeltijdSleutelSqlExpr_GeeftZelfdeUitkomstAlsCSharp()
    {
        var gevallen = new (string Categorie, string Team, string Verwacht)[]
        {
            ("1-99", "TESTCLUB O23-1", "JO23"), ("1-99", "TESTCLUB JO23-2", "JO23"), ("1-99", "O23-1", "JO23"),
            ("MO15", "TESTCLUB MO23-1", "MO15"), ("1-99", "TESTCLUB O230-1", "1-99"), ("Senioren", "TESTCLUB 1", "1-99"),
        };
        const string clubCode = "T1863";
        await using var connection = new SqlConnection(Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar));
        await connection.OpenAsync();
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync();
        await using (var ins = new SqlCommand(
            "INSERT INTO [dbo].[Speeltijden] ([Leeftijd],[Veldafmeting],[WedstrijdTotaal],[ClubCode]) VALUES ('JO23',1.00,105,@cc)", connection, tx))
        {
            ins.Parameters.AddWithValue("@cc", clubCode);
            await ins.ExecuteNonQueryAsync();
        }

        var rijen = new Dictionary<string, Speeltijd> { ["JO23"] = new() { Leeftijd = "JO23", WedstrijdTotaal = 105 } };
        foreach (var (categorie, team, verwacht) in gevallen)
        {
            await using var command = new SqlCommand(
                $"SELECT {LeeftijdNormalisatieSql.SpeeltijdSleutelSqlExpr("@cat", "@team", "@cc")}", connection, tx);
            command.Parameters.AddWithValue("@cat", categorie);
            command.Parameters.AddWithValue("@team", team);
            command.Parameters.AddWithValue("@cc", clubCode);
            var werkelijk = (string?)await command.ExecuteScalarAsync();

            Assert.Equal(verwacht, werkelijk);
            Assert.Equal(verwacht, LeeftijdNormalisatie.Normaliseer(LeeftijdNormalisatie.SpeeltijdSleutel(categorie, team, rijen)));
        }
        await tx.RollbackAsync();
    }
}
