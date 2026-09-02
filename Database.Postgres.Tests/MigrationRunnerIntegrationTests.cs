using Database.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Database.Postgres.Tests;

/// <summary>
/// Integratietests voor <see cref="MigrationRunner"/> (#821) — zelfde draaiwijze als
/// <see cref="PostgresMergeOrchestratorIntegrationTests"/> (zie die klasse-doc-comment voor de
/// wegwerpcontainer-instructies).
/// </summary>
public class MigrationRunnerIntegrationTests : IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "POSTGRES_TEST_CONNECTION_STRING";
    private string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionStringEnvVar} niet gezet — zie PostgresMergeOrchestratorIntegrationTests.");

    private string _migrationsDir = "";

    public Task InitializeAsync()
    {
        _migrationsDir = Path.Combine(Path.GetTempPath(), "migrationrunnertests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_migrationsDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_migrationsDir))
            Directory.Delete(_migrationsDir, recursive: true);
        return Task.CompletedTask;
    }

    private async Task ResetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DROP TABLE IF EXISTS schema_migrations; DROP TABLE IF EXISTS mrt_proef;", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private void SchrijfMigratie(string bestandsnaam, string sql)
        => File.WriteAllText(Path.Combine(_migrationsDir, bestandsnaam), sql);

    private async Task<int> TelToegepasteMigratiesAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM schema_migrations", connection);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task RunAsync_TweedeAanroep_IsIdempotentGeenDubbeleToepassing()
    {
        await ResetDatabaseAsync();
        SchrijfMigratie("001_baseline.sql", "CREATE TABLE mrt_proef (id INT);");

        await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);
        (await TelToegepasteMigratiesAsync()).Should().Be(1);

        await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);
        (await TelToegepasteMigratiesAsync()).Should().Be(1, "een tweede run mag geen dubbele ledger-rij toevoegen");
    }

    [PostgresFact]
    public async Task RunAsync_MeerdereMigraties_WordenInVolgordeToegepast()
    {
        await ResetDatabaseAsync();
        SchrijfMigratie("001_baseline.sql", "CREATE TABLE mrt_proef (id INT);");
        SchrijfMigratie("002_kolom_toevoegen.sql", "ALTER TABLE mrt_proef ADD COLUMN naam TEXT;");

        await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'mrt_proef' AND column_name = 'naam'",
            connection);
        var result = await cmd.ExecuteScalarAsync();
        result.Should().Be("naam", "002 moet ná 001 zijn uitgevoerd, dus de kolom moet bestaan");
    }

    [PostgresFact]
    public async Task RunAsync_GewijzigdReedsToegepastBestand_FaaltHardOpChecksumMismatch()
    {
        await ResetDatabaseAsync();
        SchrijfMigratie("001_baseline.sql", "CREATE TABLE mrt_proef (id INT);");
        await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);

        // Bestand na de feiten gewijzigd — moet hard falen, niet stilzwijgend opnieuw uitvoeren.
        SchrijfMigratie("001_baseline.sql", "CREATE TABLE mrt_proef (id INT, extra TEXT);");

        var act = async () => await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*checksum*");
    }

    [PostgresFact]
    public async Task RunAsync_MislukteMigratie_WordtNietInLedgerGeregistreerd()
    {
        await ResetDatabaseAsync();
        SchrijfMigratie("001_baseline.sql", "DIT IS GEEN GELDIGE SQL;;;");

        var act = async () => await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);
        await act.Should().ThrowAsync<PostgresException>();

        (await TelToegepasteMigratiesAsync()).Should().Be(0, "een mislukte migratie mag niet als toegepast geregistreerd staan");
    }

    /// <summary>
    /// Simuleert twee gelijktijdige runners tegen dezelfde database (expliciet vereist door de
    /// #821-review-fact-check-addendum). De advisory lock moet de tweede run laten wachten tot de
    /// eerste klaar is, zodat er nooit twee transacties tegelijk dezelfde migratie proberen toe te
    /// passen.
    /// </summary>
    [PostgresFact]
    public async Task RunAsync_TweeGelijktijdigeRunners_GeenDubbeleToepassingDoorAdvisoryLock()
    {
        await ResetDatabaseAsync();
        SchrijfMigratie("001_baseline.sql", "CREATE TABLE mrt_proef (id INT);");

        var run1 = MigrationRunner.RunAsync(ConnectionString, _migrationsDir);
        var run2 = MigrationRunner.RunAsync(ConnectionString, _migrationsDir);
        await Task.WhenAll(run1, run2);

        (await TelToegepasteMigratiesAsync()).Should().Be(1, "de advisory lock moet gelijktijdige runs serialiseren");
    }
}
