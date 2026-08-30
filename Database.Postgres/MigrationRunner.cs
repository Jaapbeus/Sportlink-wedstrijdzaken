using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Database.Postgres;

/// <summary>
/// Genummerde-migratiebestanden-runner voor de Postgres-tier (#821, Optie B).
/// <para>
/// <b>Waarom geen catalogus-introspectie zoals de SQL Server-tier.</b> Het SQL Server
/// <c>PostDeployment</c>-script (3203 regels) bestaat uitsluitend om een jaren-oude,
/// al-draaiende database stap voor stap te laten aansluiten bij tientallen historische
/// schemawijzigingen (<c>sys.columns</c>/<c>sys.objects</c>-probing per statement). Een verse
/// Postgres-club-installatie heeft die historische bagage nooit — alleen het huidige eindschema,
/// één keer. Idempotentie komt hier daarom uit een ledger-tabel (<c>schema_migrations</c>), niet
/// uit per-statement catalogus-probing.
/// </para>
/// <para>
/// <b>Checksum-verificatie</b> (uit de externe review-fact-check van #821): elk toegepast
/// migratiebestand krijgt zijn SHA-256 vastgelegd. Een latere run herberekent de checksum van elk
/// bestand en faalt hard bij een mismatch — een reeds toegepaste migratie wijzigen na de feiten is
/// een operationele fout die vroeg en luid moet falen, niet stilzwijgend een andere versie
/// opnieuw uitvoeren.
/// </para>
/// <para>
/// <b>Advisory lock</b> beschermt tegen twee gelijktijdige runners tegen dezelfde database
/// (eveneens uit de review-fact-check). <c>pg_advisory_lock</c> is sessie-gebonden: de lock komt
/// automatisch vrij als de verbinding wegvalt, dus geen wees-lock bij een crash.
/// </para>
/// <para>
/// <b><c>applied_at</c> is bewust <c>TIMESTAMPTZ</c>, niet <c>TIMESTAMP</c>.</b> #851 vond tijdens
/// het ontwerp van de epic-brede zelftest dat <c>NOW()</c> in een naïeve <c>TIMESTAMP</c>-kolom de
/// sessietijdzone gebruikt — op een container met <c>TZ=Europe/Amsterdam</c> exact de
/// GETDATE()-vs-GETUTCDATE()-regressie uit PR #246, maar dan voor Postgres. <c>TIMESTAMPTZ</c>
/// slaat het moment ondubbelzinnig op, ongeacht sessietijdzone.
/// </para>
/// </summary>
public static class MigrationRunner
{
    /// <summary>Willekeurige, stabiele advisory-lock-sleutel — vast getal, moet nooit wijzigen.</summary>
    private const long AdvisoryLockKey = 8150000821;

    public static async Task RunAsync(string connectionString, string migrationsDirectory, CancellationToken ct = default)
    {
        if (!Directory.Exists(migrationsDirectory))
            throw new DirectoryNotFoundException($"Migratiemap niet gevonden: {migrationsDirectory}");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Volgorde is kritiek: de advisory lock moet vóór ELKE DDL-aanraking van de ledger-tabel
        // genomen worden. Empirisch gevonden (#821): met de lock ná EnsureLedgerTableAsync raceten
        // twee gelijktijdige runners op de 'CREATE TABLE IF NOT EXISTS schema_migrations' zelf —
        // Postgres' IF NOT EXISTS is niet atomair tegen een gelijktijdige identieke create, en dat
        // gaf een pg_type_typname_nsp_index-unique-violation vóórdat een van beide runners de lock
        // ooit bereikte.
        await AcquireAdvisoryLockAsync(connection, ct);
        try
        {
            await EnsureLedgerTableAsync(connection, ct);
            var files = Directory.GetFiles(migrationsDirectory, "*.sql")
                .Select(f => (Path: f, Naam: Path.GetFileName(f), Volgnummer: ExtractSequenceNumber(Path.GetFileName(f))))
                .OrderBy(f => f.Volgnummer)
                .ThenBy(f => f.Naam, StringComparer.Ordinal)
                .ToList();

            foreach (var file in files)
            {
                var content = await File.ReadAllTextAsync(file.Path, ct);
                var checksum = ComputeChecksum(content);
                var existing = await GetAppliedChecksumAsync(connection, file.Naam, ct);

                if (existing is not null)
                {
                    if (!string.Equals(existing, checksum, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Migratie '{file.Naam}' is al toegepast met een andere checksum. " +
                            "Een reeds toegepast migratiebestand mag nooit achteraf gewijzigd worden — " +
                            "voeg een nieuw, opvolgend migratiebestand toe in plaats daarvan.");
                    continue; // al toegepast, checksum klopt — no-op
                }

                await using var tx = await connection.BeginTransactionAsync(ct);
                try
                {
                    await using (var cmd = new NpgsqlCommand(content, connection, tx))
                        await cmd.ExecuteNonQueryAsync(ct);

                    await using var record = new NpgsqlCommand(
                        "INSERT INTO schema_migrations (filename, checksum, applied_at) VALUES (@f, @c, NOW())",
                        connection, tx);
                    record.Parameters.AddWithValue("f", file.Naam);
                    record.Parameters.AddWithValue("c", checksum);
                    await record.ExecuteNonQueryAsync(ct);

                    await tx.CommitAsync(ct);
                }
                catch
                {
                    await tx.RollbackAsync(ct);
                    throw;
                }
            }
        }
        finally
        {
            await ReleaseAdvisoryLockAsync(connection, ct);
        }
    }

    public static int ExtractSequenceNumber(string fileName)
    {
        var digits = new string(fileName.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0)
            throw new InvalidOperationException(
                $"Migratiebestand '{fileName}' mist een numeriek volgnummer-voorvoegsel (bijv. '001_baseline.sql').");
        return int.Parse(digits);
    }

    public static string ComputeChecksum(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static async Task EnsureLedgerTableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                filename TEXT PRIMARY KEY,
                checksum TEXT NOT NULL,
                applied_at TIMESTAMPTZ NOT NULL
            );
            """, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> GetAppliedChecksumAsync(NpgsqlConnection connection, string fileName, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT checksum FROM schema_migrations WHERE filename = @f", connection);
        cmd.Parameters.AddWithValue("f", fileName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    private static async Task AcquireAdvisoryLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection);
        cmd.Parameters.AddWithValue("key", AdvisoryLockKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReleaseAdvisoryLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        cmd.Parameters.AddWithValue("key", AdvisoryLockKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
