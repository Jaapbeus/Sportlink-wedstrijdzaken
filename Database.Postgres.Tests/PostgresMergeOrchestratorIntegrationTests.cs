using Database.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Integratietests tegen een échte Postgres-instantie (#818's testplan). Draaien NIET in CI —
/// er bestaat op dit moment geen Postgres-service in de gedeelde CI-pipeline (dat is de scope
/// van #822/#823, nog niet gebouwd). Lokaal uitvoeren tegen een wegwerpcontainer:
///
///   docker run -d --name pg818 -e POSTGRES_PASSWORD=devonly -e POSTGRES_DB=sportlink_test -p 5432:5432 postgres:16
///   $env:POSTGRES_TEST_CONNECTION_STRING = "Host=localhost;Port=5432;Username=postgres;Password=devonly;Database=sportlink_test"
///   dotnet test Database.Postgres.Tests --filter FullyQualifiedName~IntegrationTests
///   docker rm -f pg818
///
/// Zodra #823 een Postgres-CI-job levert, kan de [Fact(Skip=...)] hieronder vervangen worden door
/// een omgevingsvariabele-gestuurde skip-conditie — geen wijziging aan de testlogica zelf nodig.
/// </summary>
public class PostgresMergeOrchestratorIntegrationTests : IAsyncLifetime
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
            "DROP TABLE IF EXISTS his.\"matches\"; DROP TABLE IF EXISTS stg.\"matches\"; " +
            "DROP TABLE IF EXISTS his.\"teams\"; DROP TABLE IF EXISTS stg.\"teams\"; " +
            "DROP TABLE IF EXISTS his.\"matchdetails\"; DROP TABLE IF EXISTS stg.\"matchdetails\";", connection);
        await drop.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie klasse-doc-comment) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task EnsureHisTableAsync_TweedeAanroep_IsIdempotent()
    {
        var entity = TestEntities.SingleKeyNoClub;

        await Orchestrator.EnsureHisTableAsync(entity);
        var act = async () => await Orchestrator.EnsureHisTableAsync(entity);

        await act.Should().NotThrowAsync();
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie klasse-doc-comment) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task MergeStgToHisAsync_IdentiekeDataOpnieuwGemerged_LaatMtaModifiedOngewijzigd()
    {
        var entity = TestEntities.SingleKeyNoClub;
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO stg.\"matches\" (\"matchcode\", \"datum\") VALUES ('M-1', '2026-09-01 10:00:00')", connection))
        {
            await insert.ExecuteNonQueryAsync();
        }

        await Orchestrator.MergeStgToHisAsync(entity);
        var firstModified = await ReadMtaModifiedAsync(connection, "M-1");

        // Zelfde stg-data, tweede merge — no-op verwacht.
        await Orchestrator.MergeStgToHisAsync(entity);
        var secondModified = await ReadMtaModifiedAsync(connection, "M-1");

        secondModified.Should().Be(firstModified);
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie klasse-doc-comment) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task MergeStgToHisAsync_GewijzigdeData_WerktMtaModifiedBij()
    {
        var entity = TestEntities.SingleKeyNoClub;
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        async Task SetStgDatumAsync(string datum)
        {
            await using var upsertStg = new NpgsqlCommand(
                "DELETE FROM stg.\"matches\"; INSERT INTO stg.\"matches\" (\"matchcode\", \"datum\") VALUES ('M-2', @datum)",
                connection);
            upsertStg.Parameters.AddWithValue("datum", DateTime.Parse(datum));
            await upsertStg.ExecuteNonQueryAsync();
        }

        await SetStgDatumAsync("2026-09-01 10:00:00");
        await Orchestrator.MergeStgToHisAsync(entity);
        var firstModified = await ReadMtaModifiedAsync(connection, "M-2");

        await Task.Delay(50); // zorg dat NOW() daadwerkelijk vooruitgaat tussen de twee merges
        await SetStgDatumAsync("2026-09-01 11:00:00");
        await Orchestrator.MergeStgToHisAsync(entity);
        var secondModified = await ReadMtaModifiedAsync(connection, "M-2");

        secondModified.Should().BeAfter(firstModified);
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie klasse-doc-comment) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task EnsureHisTableAsync_DubbeleBusinessKeyDirectInHis_WordtGeweigerdDoorUniqueIndex()
    {
        var entity = TestEntities.SingleKeyNoClub;
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO his.\"matches\" (\"matchcode\", \"mta_inserted\", \"mta_modified\") VALUES ('M-3', NOW(), NOW())",
            connection))
        {
            await insert.ExecuteNonQueryAsync();
        }

        var act = async () =>
        {
            await using var duplicate = new NpgsqlCommand(
                "INSERT INTO his.\"matches\" (\"matchcode\", \"mta_inserted\", \"mta_modified\") VALUES ('M-3', NOW(), NOW())",
                connection);
            await duplicate.ExecuteNonQueryAsync();
        };

        await act.Should().ThrowAsync<PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.UniqueViolation);
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie klasse-doc-comment) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task MergeStgToHisAsync_NullInEenDeelVanDeCompositeBusinessKey_WerktBestaandeRijBijInPlaatsVanDuplicaat()
    {
        // Dit is de directe empirische proef van het #818-addendum: teams.poulecode is NULL-baar
        // in productie. Zonder de COALESCE-gebaseerde synthetische bk_-kolom zou Postgres' eigen
        // UNIQUE/ON CONFLICT-gedrag (NULL = distinct) hier een tweede rij invoegen i.p.v. de
        // bestaande bij te werken.
        var entity = TestEntities.MultiKeyNoClub;
        await Orchestrator.RecreateStgTableAsync(entity);
        await Orchestrator.EnsureHisTableAsync(entity);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        async Task SeedStgAsync(string teamnaam)
        {
            await using var clear = new NpgsqlCommand("DELETE FROM stg.\"teams\"", connection);
            await clear.ExecuteNonQueryAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO stg.\"teams\" (\"teamcode\", \"lokaleteamcode\", \"poulecode\", \"teamnaam\") " +
                "VALUES ('T-1', 'JO13-1', NULL, @teamnaam)", connection);
            insert.Parameters.AddWithValue("teamnaam", teamnaam);
            await insert.ExecuteNonQueryAsync();
        }

        await SeedStgAsync("Eerste naam");
        await Orchestrator.MergeStgToHisAsync(entity);

        await SeedStgAsync("Tweede naam — bijgewerkt");
        await Orchestrator.MergeStgToHisAsync(entity);

        await using var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM his.\"teams\" WHERE \"teamcode\" = 'T-1' AND \"lokaleteamcode\" = 'JO13-1'", connection);
        var rowCount = (long)(await countCmd.ExecuteScalarAsync())!;
        rowCount.Should().Be(1, "een NULL-waarde in poulecode mag geen tweede rij opleveren");

        await using var nameCmd = new NpgsqlCommand(
            "SELECT \"teamnaam\" FROM his.\"teams\" WHERE \"teamcode\" = 'T-1' AND \"lokaleteamcode\" = 'JO13-1'", connection);
        var naam = (string)(await nameCmd.ExecuteScalarAsync())!;
        naam.Should().Be("Tweede naam — bijgewerkt", "de bestaande rij moet zijn bijgewerkt, niet gedupliceerd");
    }

    [Fact(Skip = "Vereist lokale Postgres-instantie (zie klasse-doc-comment) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task EnsureHisTableAsync_AlleDrieBekendeEntiteiten_CreërenDaadwerkelijkTegenEchtePostgres()
    {
        // Het #818-acceptatiecriterium eist "werkende CREATE TABLE-statements voor alle
        // entiteiten" — dit bewijst het voor de drie daadwerkelijk bestaande entiteiten
        // (KnownEntities), niet alleen voor de synthetische testfixtures hierboven. matchdetails
        // is de zwaarste proef: 62 kolommen, DATE/TIME-typen, en een INTEGER (geen VARCHAR)
        // business-key-kolom.
        foreach (var entity in KnownEntities.All)
        {
            await Orchestrator.RecreateStgTableAsync(entity);
            await Orchestrator.EnsureHisTableAsync(entity);
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var insertMatchDetails = new NpgsqlCommand(
            "INSERT INTO stg.\"matchdetails\" (\"WedstrijdCode\", \"MatchDate\", \"Aanvangstijd\") " +
            "VALUES (12345, '2026-09-01', '14:30:00')", connection);
        await insertMatchDetails.ExecuteNonQueryAsync();

        await Orchestrator.MergeStgToHisAsync(KnownEntities.MatchDetails);

        await using var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM his.\"matchdetails\" WHERE \"WedstrijdCode\" = 12345", connection);
        var rowCount = (long)(await countCmd.ExecuteScalarAsync())!;
        rowCount.Should().Be(1);
    }

    private static async Task<DateTime> ReadMtaModifiedAsync(NpgsqlConnection connection, string matchcode)
    {
        await using var command = new NpgsqlCommand(
            "SELECT \"mta_modified\" FROM his.\"matches\" WHERE \"matchcode\" = @code", connection);
        command.Parameters.AddWithValue("code", matchcode);
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }
}
