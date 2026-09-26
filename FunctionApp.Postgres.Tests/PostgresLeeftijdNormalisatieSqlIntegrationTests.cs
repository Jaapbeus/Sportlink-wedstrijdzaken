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
}
