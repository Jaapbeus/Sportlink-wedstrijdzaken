using Database.Postgres;
using Database.Postgres.Tests;
using AwesomeAssertions;
using Npgsql;
using Planner.Shared;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Voert <see cref="PostgresLeeftijdNormalisatie.SqlExpr"/> daadwerkelijk uit tegen een levende
/// Postgres-instantie (i.p.v. alleen de gegenereerde SQL-tekst te inspecteren) en toetst de
/// uitkomst tegen <see cref="LeeftijdNormalisatie.Normaliseer"/> — de C#-tegenhanger waarmee deze
/// SQL-expressie volgens de klasse-doc-comment altijd hetzelfde resultaat moet geven.
///
/// <para>
/// <b>Waarom dit nodig was (#1332).</b> Die invariant stond alleen als commentaar; er was geen test
/// die hem afdwong. Daardoor kon de SQL-expressie stilzwijgend "Onder 13 Meiden" naar de
/// niet-bestaande sleutel "MOOnder 13" normaliseren (i.p.v. "MO13") zonder dat enige testsuite dat
/// opmerkte — <c>LeeftijdNormalisatieSqlTests</c> (SQL Server-tier) inspecteert alleen of de
/// gegenereerde teksttekst bepaalde substrings bevat, niet wat de expressie daadwerkelijk oplevert.
/// </para>
/// </summary>
public class PostgresLeeftijdNormalisatieSqlIntegrationTests
{
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresTheory]
    [InlineData("Senioren")]
    [InlineData("Senioren Vrouwen")]
    [InlineData("JO15 Meiden")]
    [InlineData("JO9 Meiden")]
    [InlineData("Onder 13")]
    [InlineData("Onder 8")]
    [InlineData("Meisjes Onder 15")]
    [InlineData("Onder 13 Meiden")]
    [InlineData("Onder 11 Meiden")]
    [InlineData("Onder 15 Meiden")]
    [InlineData("Onder 17 Meiden")]
    [InlineData("MO13")]
    [InlineData("MO 13")]
    [InlineData("MOJO13")]
    [InlineData("1-99")]
    [InlineData("VR")]
    [InlineData("JO13")]
    [InlineData("G")]
    public async Task SqlExpr_GeeftZelfdeUitkomstAlsDeCSharpNormalisatie(string invoer)
    {
        var verwacht = LeeftijdNormalisatie.Normaliseer(invoer);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        var sql = $"SELECT {PostgresLeeftijdNormalisatie.SqlExpr("@invoer")}";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("invoer", invoer);
        var werkelijk = (string?)await cmd.ExecuteScalarAsync();

        werkelijk.Should().Be(verwacht,
            $"'{invoer}' moet via SQL en via C# naar dezelfde Speeltijden-sleutel normaliseren");
    }

    [PostgresFact]
    public async Task SqlExpr_OnderDertienMeiden_MaptNaarMo13()
    {
        // Directe regressietest voor #1332: dit exacte format kwam voor bij een echte club en
        // liet de wedstrijd zonder foutmelding uit de Dagplanning-Gantt verdwijnen.
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        var sql = $"SELECT {PostgresLeeftijdNormalisatie.SqlExpr("'Onder 13 Meiden'")}";
        await using var cmd = new NpgsqlCommand(sql, conn);
        var werkelijk = (string?)await cmd.ExecuteScalarAsync();

        werkelijk.Should().Be("MO13");
    }

    [Fact]
    public void SpeeltijdSleutelSqlExpr_GeeftO23DeJo23SleutelMetTerugval()
    {
        var expr = PostgresLeeftijdNormalisatie.SpeeltijdSleutelSqlExpr("t.leeftijdscategorie", "m.teamnaam", "@clubcode");

        expr.Should().Contain("m.teamnaam ~* '(^|[^[:alnum:]])J?O23([^0-9]|$)'");
        expr.Should().Contain("jo23.wedstrijdtotaal > 0");
        expr.Should().Contain("jo23.clubcode = @clubcode");
        expr.Should().Contain("THEN 'JO23'");
        expr.Should().Contain(PostgresLeeftijdNormalisatie.SqlExpr("t.leeftijdscategorie"), "buiten O23 blijft de gewone normalisatie gelden");
    }

    [Fact]
    public void PlannerMatchRepository_GebruiktDeTeamnaamBewareSleutelInAlleSpeeltijdenJoins()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        var bron = File.ReadAllText(Path.Combine(dir!.FullName, "FunctionApp.Postgres/Planner/Repositories/PlannerMatchRepository.cs"));

        bron.Should().NotContain("PostgresLeeftijdNormalisatie.SqlExpr(\"t.leeftijdscategorie\")", "een join op speeltijden hoort de teamnaam te kennen (#1863)");
        bron.Should().Contain("SpeeltijdSleutelSqlExpr");
    }

    [Fact]
    public void PlannerView_KentDeO23Regel()
    {
        // #1863: de gegenereerde view kiest voor een O23-team de JO23-speeltijd (met terugval op de categorie).
        var view = PostgresPlannerViewGenerator.CreateView;

        view.Should().Contain("m.teamnaam ~* '(^|[^[:alnum:]])J?O23([^0-9]|$)'")
            .And.Contain("jo23.wedstrijdtotaal > 0").And.Contain("THEN 'JO23'");
    }

    /// <summary>
    /// #1863: voert de teamnaam-bewuste expressie echt uit en toetst hem tegen
    /// <see cref="LeeftijdNormalisatie.SpeeltijdSleutel"/>. Eigen wegwerp-clubcode, binnen een transactie die terugdraait.
    /// </summary>
    [PostgresFact]
    public async Task SpeeltijdSleutelSqlExpr_GeeftZelfdeUitkomstAlsCSharp()
    {
        var gevallen = new (string Categorie, string Team, string Verwacht)[]
        {
            ("1-99", "TESTCLUB O23-1", "JO23"), ("1-99", "TESTCLUB JO23-2", "JO23"), ("1-99", "O23-1", "JO23"),
            ("MO15", "TESTCLUB MO23-1", "MO15"), ("1-99", "TESTCLUB O230-1", "1-99"), ("Senioren", "TESTCLUB 1", "1-99"),
        };
        var rijen = new Dictionary<string, Speeltijd> { ["JO23"] = new() { Leeftijd = "JO23", WedstrijdTotaal = 105 } };

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO public.speeltijden (leeftijd, veldafmeting, wedstrijdtotaal, clubcode) VALUES ('JO23', 1.00, 105, 'T1863')", conn, tx))
            await ins.ExecuteNonQueryAsync();

        foreach (var (categorie, team, verwacht) in gevallen)
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT {PostgresLeeftijdNormalisatie.SpeeltijdSleutelSqlExpr("@cat", "@team", "'T1863'")}", conn, tx);
            cmd.Parameters.AddWithValue("cat", categorie);
            cmd.Parameters.AddWithValue("team", team);
            var werkelijk = (string?)await cmd.ExecuteScalarAsync();

            werkelijk.Should().Be(verwacht, $"'{team}' ({categorie})");
            LeeftijdNormalisatie.Normaliseer(LeeftijdNormalisatie.SpeeltijdSleutel(categorie, team, rijen)).Should().Be(verwacht);
        }
        await tx.RollbackAsync();
    }
}
