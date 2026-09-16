using Database.Postgres;
using AwesomeAssertions;
using Npgsql;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Integratietests voor de #1193-reconciliatie (<see cref="PostgresMergeOrchestrator.ReconcileFullScopeAsync"/>/
/// <see cref="PostgresMergeOrchestrator.ReconcileWindowedAsync"/>) tegen een échte Postgres-instantie.
/// Zelfde opzet als <see cref="PostgresMergeOrchestratorIntegrationTests"/> — zie die klasse-doc-comment
/// voor hoe lokaal een wegwerpcontainer op te zetten.
/// </summary>
public class PostgresReconciliationIntegrationTests : IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "POSTGRES_TEST_CONNECTION_STRING";
    private string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    private PostgresMergeOrchestrator Orchestrator => new(ConnectionString);

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand(
            "DROP TABLE IF EXISTS his.\"matches\" CASCADE; DROP TABLE IF EXISTS stg.\"matches\" CASCADE; " +
            "DROP TABLE IF EXISTS his.\"teams\" CASCADE; DROP TABLE IF EXISTS stg.\"teams\" CASCADE;", connection);
        await drop.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── ReconcileFullScopeAsync (teams — geen datumvenster) ────────────────────────────────

    [PostgresFact]
    public async Task ReconcileFullScopeAsync_TeamNietMeerInStg_WordtGemarkeerdAlsVerwijderd()
    {
        var entity = TestEntities.MultiKeyWithClub; // entityName "teams"
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // his heeft twee teams voor CLUBA; stg (de zojuist geladen sync) heeft er nog maar één —
        // T-2 is dus verdwenen bij Sportlink.
        await SeedHisTeamAsync(connection, "T-1", "CLUBA");
        await SeedHisTeamAsync(connection, "T-2", "CLUBA");
        await SeedStgTeamAsync(connection, "T-1", "CLUBA");

        var aantal = await Orchestrator.ReconcileFullScopeAsync(entity, "CLUBA");

        aantal.Should().Be(1);
        (await IsGemarkeerdAlsVerwijderdAsync(connection, "teams", "T-1")).Should().BeFalse("T-1 staat nog in stg");
        (await IsGemarkeerdAlsVerwijderdAsync(connection, "teams", "T-2")).Should().BeTrue("T-2 ontbreekt in stg");
    }

    [PostgresFact]
    public async Task ReconcileFullScopeAsync_AndereClub_WordtNietGeraakt()
    {
        var entity = TestEntities.MultiKeyWithClub;
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // CLUBB wordt deze run niet gesynchroniseerd (stg is leeg voor CLUBB) — reconciliatie voor
        // CLUBA mag CLUBB's teams nooit aanraken.
        await SeedHisTeamAsync(connection, "T-9", "CLUBB");
        await SeedStgTeamAsync(connection, "T-1", "CLUBA");

        var aantal = await Orchestrator.ReconcileFullScopeAsync(entity, "CLUBA");

        aantal.Should().Be(0);
        (await IsGemarkeerdAlsVerwijderdAsync(connection, "teams", "T-9")).Should().BeFalse(
            "reconciliatie voor CLUBA mag nooit CLUBB-rijen raken");
    }

    [PostgresFact]
    public async Task ReconcileFullScopeAsync_TweedeAanroepZonderWijziging_IsIdempotent()
    {
        var entity = TestEntities.MultiKeyWithClub;
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await SeedHisTeamAsync(connection, "T-1", "CLUBA");
        // stg blijft leeg voor CLUBA -> T-1 verdwijnt.

        var eersteRonde = await Orchestrator.ReconcileFullScopeAsync(entity, "CLUBA");
        var tweedeRonde = await Orchestrator.ReconcileFullScopeAsync(entity, "CLUBA");

        eersteRonde.Should().Be(1);
        tweedeRonde.Should().Be(0, "T-1 is al gemarkeerd — mta_deleted IS NULL sluit hem de tweede keer uit");
    }

    // ── ReconcileWindowedAsync (matches — met datumvenster) ────────────────────────────────

    [PostgresFact]
    public async Task ReconcileWindowedAsync_MatchNietMeerInStgBinnenVenster_WordtGemarkeerdAlsVerwijderd()
    {
        var entity = TestEntities.SingleKeyWithClub; // entityName "matches"
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Simuleert precies het #1193-scenario: drie wedstrijden stonden in his binnen dezelfde
        // week, de nieuwste sync (stg) levert de eerste en de laatste weer op maar niet de
        // middelste — die is bij Sportlink verdwenen. Zijn datum (11 okt) valt WEL binnen het
        // venster dat deze sync-run daadwerkelijk bevraagd heeft: dat venster wordt afgeleid uit
        // stg's eigen MIN/MAX datum (10 okt .. 12 okt), en 11 okt ligt daarbinnen ook al staat er
        // zelf geen stg-rij op die datum.
        await SeedHisMatchAsync(connection, "M-1", "CLUBA", "2026-10-10");
        await SeedHisMatchAsync(connection, "M-2", "CLUBA", "2026-10-11");
        await SeedHisMatchAsync(connection, "M-3", "CLUBA", "2026-10-12");
        await SeedStgMatchAsync(connection, "M-1", "CLUBA", "2026-10-10");
        await SeedStgMatchAsync(connection, "M-3", "CLUBA", "2026-10-12");

        var aantal = await Orchestrator.ReconcileWindowedAsync(entity, "CLUBA", "datum");

        aantal.Should().Be(1);
        (await IsGemarkeerdAlsVerwijderdAsync(connection, "matches", "M-1")).Should().BeFalse();
        (await IsGemarkeerdAlsVerwijderdAsync(connection, "matches", "M-2")).Should().BeTrue();
        (await IsGemarkeerdAlsVerwijderdAsync(connection, "matches", "M-3")).Should().BeFalse();
    }

    [PostgresFact]
    public async Task ReconcileWindowedAsync_MatchBuitenVenster_BlijftOngemarkeerd()
    {
        var entity = TestEntities.SingleKeyWithClub;
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // his heeft een wedstrijd ver in de toekomst (buiten wat deze sync-run heeft opgehaald) en
        // stg bevat alleen wedstrijden uit een veel nabijere periode. De verre wedstrijd mag NOOIT
        // gereconcilieerd worden, ook al staat hij niet in stg — hij viel simpelweg buiten het
        // venster van deze run.
        await SeedHisMatchAsync(connection, "M-VER", "CLUBA", "2027-06-01");
        await SeedStgMatchAsync(connection, "M-1", "CLUBA", "2026-10-10");

        var aantal = await Orchestrator.ReconcileWindowedAsync(entity, "CLUBA", "datum");

        aantal.Should().Be(0, "de MIN/MAX van stg bakent het venster af — 2027-06-01 valt daarbuiten");
        (await IsGemarkeerdAlsVerwijderdAsync(connection, "matches", "M-VER")).Should().BeFalse();
    }

    [PostgresFact]
    public async Task ReconcileWindowedAsync_LegeStgVoorDezeClub_ReconcilieertNiets()
    {
        var entity = TestEntities.SingleKeyWithClub;
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // stg is leeg voor CLUBA (bijv. een mislukte/overgeslagen fetch) — reconciliatie moet dan
        // NIETS doen, nooit het hele his-bestand voor deze club als verdwenen markeren.
        await SeedHisMatchAsync(connection, "M-1", "CLUBA", "2026-10-10");

        var aantal = await Orchestrator.ReconcileWindowedAsync(entity, "CLUBA", "datum");

        aantal.Should().Be(0);
        (await IsGemarkeerdAlsVerwijderdAsync(connection, "matches", "M-1")).Should().BeFalse();
    }

    [PostgresFact]
    public async Task ReconcileWindowedAsync_AndereClub_WordtNietGeraakt()
    {
        var entity = TestEntities.SingleKeyWithClub;
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await SeedHisMatchAsync(connection, "M-ANDER", "CLUBB", "2026-10-10");
        await SeedStgMatchAsync(connection, "M-1", "CLUBA", "2026-10-10");

        var aantal = await Orchestrator.ReconcileWindowedAsync(entity, "CLUBA", "datum");

        aantal.Should().Be(0);
        (await IsGemarkeerdAlsVerwijderdAsync(connection, "matches", "M-ANDER")).Should().BeFalse();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    private static async Task SeedHisTeamAsync(NpgsqlConnection connection, string teamcode, string clubcode)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO his.\"teams\" (\"teamcode\", \"lokaleteamcode\", \"poulecode\", \"teamnaam\", \"clubcode\", " +
            "\"mta_inserted\", \"mta_modified\") VALUES (@teamcode, 'L-1', 'P-1', 'Team ' || @teamcode, @clubcode, NOW(), NOW())",
            connection);
        insert.Parameters.AddWithValue("teamcode", teamcode);
        insert.Parameters.AddWithValue("clubcode", clubcode);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task SeedStgTeamAsync(NpgsqlConnection connection, string teamcode, string clubcode)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO stg.\"teams\" (\"teamcode\", \"lokaleteamcode\", \"poulecode\", \"teamnaam\", \"clubcode\") " +
            "VALUES (@teamcode, 'L-1', 'P-1', 'Team ' || @teamcode, @clubcode)", connection);
        insert.Parameters.AddWithValue("teamcode", teamcode);
        insert.Parameters.AddWithValue("clubcode", clubcode);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task SeedHisMatchAsync(NpgsqlConnection connection, string matchcode, string clubcode, string datum)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO his.\"matches\" (\"matchcode\", \"datum\", \"clubcode\", \"mta_inserted\", \"mta_modified\") " +
            "VALUES (@matchcode, @datum, @clubcode, NOW(), NOW())", connection);
        insert.Parameters.AddWithValue("matchcode", matchcode);
        insert.Parameters.AddWithValue("datum", DateTime.Parse(datum));
        insert.Parameters.AddWithValue("clubcode", clubcode);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task SeedStgMatchAsync(NpgsqlConnection connection, string matchcode, string clubcode, string datum)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO stg.\"matches\" (\"matchcode\", \"datum\", \"clubcode\") VALUES (@matchcode, @datum, @clubcode)",
            connection);
        insert.Parameters.AddWithValue("matchcode", matchcode);
        insert.Parameters.AddWithValue("datum", DateTime.Parse(datum));
        insert.Parameters.AddWithValue("clubcode", clubcode);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsGemarkeerdAlsVerwijderdAsync(NpgsqlConnection connection, string table, string businessKeyPrefix)
    {
        var kolom = table == "teams" ? "teamcode" : "matchcode";
        await using var command = new NpgsqlCommand(
            $"SELECT \"mta_deleted\" FROM his.\"{table}\" WHERE \"{kolom}\" = @code", connection);
        command.Parameters.AddWithValue("code", businessKeyPrefix);
        var result = await command.ExecuteScalarAsync();
        return result is not null and not DBNull;
    }
}
