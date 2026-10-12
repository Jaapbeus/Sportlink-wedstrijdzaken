using AwesomeAssertions;
using Npgsql;
using Planner.Shared.Avg;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Seizoensretentie van de persoonskolommen in de historietabellen (#1698) tegen een echte
/// Postgres-instantie, plus RLS op de door de generator aangemaakte his- en stg-tabellen. Zelfde
/// opzet als <see cref="PostgresReconciliationIntegrationTests"/>. Testdata uitsluitend volgens
/// ARCHITECTUUR.md §8.1.5. De SQL Server-tegenhanger is <c>SqlServerHisRetentieIntegrationTests</c>.
/// </summary>
public class PostgresHisRetentieIntegrationTests : IAsyncLifetime
{
    private static readonly DateOnly Grens = new(2026, 6, 30);
    private string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException($"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand(
            "DROP TABLE IF EXISTS his.\"matches\" CASCADE; DROP TABLE IF EXISTS his.\"matchdetails\" CASCADE; " +
            "DROP TABLE IF EXISTS stg.\"matches\" CASCADE;", connection);
        await drop.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task WisTotEnMet_MaaktAlleenPersoonskolommenVanAfgerondeSeizoenenLeeg_EnIsIdempotent()
    {
        var orchestrator = new PostgresMergeOrchestrator(ConnectionString);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(KnownEntities.MatchDetails);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await VoegMatchToe(connection, 1, "2026-06-30T14:30:00+02:00"); // laatste dag van het afgeronde seizoen
        await VoegMatchToe(connection, 2, "2026-07-01T14:30:00+02:00"); // nieuw seizoen
        await VoegMatchToe(connection, 3, "onbekend");                   // afwijkende vorm: niet raden
        await VoegDetailToe(connection, 11, new DateOnly(2026, 5, 2), null);
        await VoegDetailToe(connection, 12, new DateOnly(2026, 8, 2), null);
        await VoegDetailToe(connection, 13, null, new DateTime(2026, 4, 4, 12, 0, 0)); // alleen matchdatetime
        await VoegDetailToe(connection, 14, null, null);                                // geen datum

        var (matches, details) = await PostgresHisRetentie.WisTotEnMetAsync(connection, Grens);

        matches.Should().Be(1);
        details.Should().Be(2);
        (await Tekst(connection, "matches", "wedstrijdcode", 1, "scheidsrechter")).Should().BeNull();
        (await Tekst(connection, "matches", "wedstrijdcode", 1, "rijders")).Should().BeNull();
        (await Tekst(connection, "matches", "wedstrijdcode", 1, "thuisteam")).Should().Be("AllStars JO11-1", "de wedstrijd zelf blijft staan");
        (await Tekst(connection, "matches", "wedstrijdcode", 2, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(connection, "matches", "wedstrijdcode", 3, "scheidsrechter")).Should().Be("Jan de Vries");
        (await Tekst(connection, "matchdetails", "interncode", 11, "thuisteamemail")).Should().BeNull();
        (await Tekst(connection, "matchdetails", "interncode", 11, "opmerkingen")).Should().BeNull();
        (await Tekst(connection, "matchdetails", "interncode", 11, "thuisteam")).Should().Be("AllStars JO11-1");
        (await Tekst(connection, "matchdetails", "interncode", 12, "rijder")).Should().Be("Jan de Vries");
        (await Tekst(connection, "matchdetails", "interncode", 13, "rijder")).Should().BeNull();
        (await Tekst(connection, "matchdetails", "interncode", 14, "rijder")).Should().Be("Jan de Vries");

        (await PostgresHisRetentie.WisTotEnMetAsync(connection, Grens)).Should().Be((0, 0), "een tweede run raakt niets meer");
    }

    [PostgresFact]
    public async Task WisTotEnMet_ZonderHistorietabellen_DoetNiets()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        (await PostgresHisRetentie.WisTotEnMetAsync(connection, Grens)).Should().Be((0, 0));
    }

    [PostgresFact]
    public async Task LeesSeizoenseinden_LeestDeSeizoenstabel()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var telling = new NpgsqlCommand("SELECT COUNT(*) FROM public.season WHERE dateuntil IS NOT NULL", connection);
        var verwacht = Convert.ToInt32(await telling.ExecuteScalarAsync());

        (await PostgresHisRetentie.LeesSeizoenseindenAsync(connection)).Should().HaveCount(verwacht);
    }

    [PostgresFact]
    public async Task Generator_ZetRowLevelSecurityAanOpHisEnStg_OokBijHerhaling()
    {
        var orchestrator = new PostgresMergeOrchestrator(ConnectionString);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches);
        await orchestrator.EnsureHisTableAsync(KnownEntities.Matches); // idempotent, tweede keer no-op
        await orchestrator.RecreateStgTableAsync(KnownEntities.Matches);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        (await HeeftRls(connection, "his", "matches")).Should().BeTrue();
        (await HeeftRls(connection, "stg", "matches")).Should().BeTrue();
    }

    private static async Task VoegMatchToe(NpgsqlConnection c, long code, string datum)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO his.\"matches\" (\"wedstrijdcode\", \"wedstrijddatum\", \"thuisteam\", \"scheidsrechter\", " +
            "\"scheidsrechters\", \"rijders\", \"clubcode\", \"mta_inserted\", \"mta_modified\") " +
            "VALUES (@code, @datum, 'AllStars JO11-1', 'Jan de Vries', 'Jan de Vries', 'Jan de Vries', 'ALLSTARS', NOW(), NOW())", c);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("datum", datum);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task VoegDetailToe(NpgsqlConnection c, int interncode, DateOnly? datum, DateTime? datumtijd)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO his.\"matchdetails\" (\"wedstrijdcode\", \"interncode\", \"matchdate\", \"matchdatetime\", \"thuisteam\", " +
            "\"rijder\", \"opmerkingen\", \"thuisteamemail\", \"clubcode\", \"mta_inserted\", \"mta_modified\") " +
            "VALUES (1, @interncode, @datum, @datumtijd, 'AllStars JO11-1', 'Jan de Vries', 'Jan de Vries rijdt', " +
            "'trainer@voorbeeld.nl', 'ALLSTARS', NOW(), NOW())", c);
        cmd.Parameters.AddWithValue("interncode", interncode);
        cmd.Parameters.Add(new NpgsqlParameter("datum", NpgsqlTypes.NpgsqlDbType.Date) { Value = (object?)datum ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("datumtijd", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = (object?)datumtijd ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> Tekst(NpgsqlConnection c, string tabel, string sleutel, long waarde, string kolom)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {PostgresIdentifier.Quote(kolom)} FROM his.{PostgresIdentifier.Quote(tabel)} WHERE {PostgresIdentifier.Quote(sleutel)} = @w", c);
        cmd.Parameters.AddWithValue("w", waarde);
        var resultaat = await cmd.ExecuteScalarAsync();
        return resultaat is DBNull or null ? null : (string)resultaat;
    }

    private static async Task<bool> HeeftRls(NpgsqlConnection c, string schema, string tabel)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT c.relrowsecurity FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = @s AND c.relname = @t", c);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", tabel);
        return await cmd.ExecuteScalarAsync() is true;
    }
}
