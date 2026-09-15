using Database.Postgres;
using AwesomeAssertions;
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
    /// #1112: een ledger-rij die vanaf een CRLF-checkout is gevuld (rauwe SHA-256 over CRLF-bytes)
    /// mag een latere run vanaf een LF-checkout niet blokkeren. De runner herkent het artefact,
    /// schrijft de rij om naar de genormaliseerde waarde en rapporteert dat — zonder de migratie
    /// opnieuw uit te voeren.
    /// </summary>
    [PostgresFact]
    public async Task RunAsync_LedgerMetRauweCrlfChecksum_WordtGenormaliseerdNietGeblokkeerd()
    {
        await ResetDatabaseAsync();
        var lf = "CREATE TABLE mrt_proef (\n    id INT\n);\n";
        SchrijfMigratie("001_baseline.sql", lf);
        await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);

        // Simuleer de oude ledger-waarde van vóór #1112, geschreven vanaf een Windows-werkmap.
        var crlfRauw = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(lf.Replace("\n", "\r\n")))).ToLowerInvariant();
        await ZetLedgerChecksumAsync("001_baseline.sql", crlfRauw);

        var result = await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);

        result.ChecksumNormalized.Should().Equal("001_baseline.sql");
        result.Applied.Should().BeEmpty("de migratie mag niet opnieuw uitgevoerd worden");
        (await LeesLedgerChecksumAsync("001_baseline.sql")).Should().Be(MigrationRunner.ComputeChecksum(lf));
        (await TelToegepasteMigratiesAsync()).Should().Be(1);

        // Derde run: nu klopt de ledger en is er niets meer te normaliseren.
        var tweede = await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);
        tweede.ChecksumNormalized.Should().BeEmpty();
        tweede.AlreadyApplied.Should().Equal("001_baseline.sql");
    }

    /// <summary>#1112: het bestand zelf als CRLF op schijf (Windows-werkmap) tegen een ledger die
    /// vanaf LF is gevuld — de normalisatie moet ook die richting stil goed laten gaan, zonder
    /// reparatie (de ledger klopt al).</summary>
    [PostgresFact]
    public async Task RunAsync_BestandAlsCrlfOpSchijf_TegenLfLedger_IsGewoonAlToegepast()
    {
        await ResetDatabaseAsync();
        var lf = "CREATE TABLE mrt_proef (\n    id INT\n);\n";
        SchrijfMigratie("001_baseline.sql", lf);
        await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);

        SchrijfMigratie("001_baseline.sql", lf.Replace("\n", "\r\n"));
        var result = await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);

        result.AlreadyApplied.Should().Equal("001_baseline.sql");
        result.ChecksumNormalized.Should().BeEmpty();
        result.Applied.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task RunAsync_RapporteertNieuwEnAlToegepastAfzonderlijk()
    {
        await ResetDatabaseAsync();
        SchrijfMigratie("001_baseline.sql", "CREATE TABLE mrt_proef (id INT);");
        var eerste = await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);
        eerste.Applied.Should().Equal("001_baseline.sql");

        SchrijfMigratie("002_kolom.sql", "ALTER TABLE mrt_proef ADD COLUMN naam TEXT;");
        var tweede = await MigrationRunner.RunAsync(ConnectionString, _migrationsDir);
        tweede.AlreadyApplied.Should().Equal("001_baseline.sql");
        tweede.Applied.Should().Equal("002_kolom.sql");
    }

    private async Task ZetLedgerChecksumAsync(string bestandsnaam, string checksum)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE schema_migrations SET checksum = @c WHERE filename = @f", connection);
        cmd.Parameters.AddWithValue("c", checksum);
        cmd.Parameters.AddWithValue("f", bestandsnaam);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> LeesLedgerChecksumAsync(string bestandsnaam)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT checksum FROM schema_migrations WHERE filename = @f", connection);
        cmd.Parameters.AddWithValue("f", bestandsnaam);
        return (await cmd.ExecuteScalarAsync()) as string;
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
