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
/// <b>Checksum is regeleinde-onafhankelijk (#1112).</b> <c>.gitattributes</c> zet <c>* text=auto</c>,
/// dus een Windows-checkout met <c>core.autocrlf=true</c> levert dezelfde migratie met CRLF op als
/// een macOS/Linux/CI-checkout met LF. Een checksum over de rauwe bytes legde dan een
/// platformafhankelijke waarde in de ledger vast, en elke volgende run vanaf het andere platform
/// faalde hard op "andere checksum" — voor hetzelfde bestand. Daarom wordt vóór het hashen
/// <c>\r\n</c> naar <c>\n</c> genormaliseerd. Een ledger-rij die nog een rauwe CRLF-checksum
/// draagt, wordt bij de eerstvolgende run herkend (de CRLF-variant van het huidige bestand geeft
/// exact die oude waarde — dus bewezen geen inhoudelijke wijziging) en éénmalig naar de
/// genormaliseerde waarde omgeschreven; zie <see cref="IsLineEndingVariant"/>. Elke andere
/// mismatch blijft een harde fout.
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

    /// <param name="onMigratieStart">
    /// #1225: wordt aangeroepen met de bestandsnaam vlak vóór elk migratiebestand wordt behandeld.
    /// Hiermee weet een aanroeper bij een exception wélke stap faalde, zónder dat de exception zelf
    /// hoeft te worden ingepakt of gelezen — de tekst van een databasefout draagt host of
    /// gebruikersnaam mee en mag niet in een publiek CI-log belanden (zie
    /// <see cref="MigratieFoutRapportage"/>). Blijft de callback ongeroepen, dan ging het mis vóór
    /// de eerste stap, bijvoorbeeld bij het openen van de verbinding.
    /// </param>
    public static async Task<MigrationRunResult> RunAsync(string connectionString, string migrationsDirectory, CancellationToken ct = default, Action<string>? onMigratieStart = null)
    {
        var result = new MigrationRunResult();
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
                onMigratieStart?.Invoke(file.Naam);
                var uitkomst = await ApplyMigrationFileAsync(connection, file.Path, file.Naam, ct);
                switch (uitkomst)
                {
                    case MigrationOutcome.Applied: result.Applied.Add(file.Naam); break;
                    case MigrationOutcome.AlreadyApplied: result.AlreadyApplied.Add(file.Naam); break;
                    case MigrationOutcome.ChecksumNormalized: result.ChecksumNormalized.Add(file.Naam); break;
                }
            }
        }
        finally
        {
            await ReleaseAdvisoryLockAsync(connection, ct);
        }

        return result;
    }

    private enum MigrationOutcome { Applied, AlreadyApplied, ChecksumNormalized }

    private static async Task<MigrationOutcome> ApplyMigrationFileAsync(NpgsqlConnection connection, string filePath, string fileName, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var checksum = ComputeChecksum(content);
        var existing = await GetAppliedChecksumAsync(connection, fileName, ct);

        if (existing is not null)
        {
            if (string.Equals(existing, checksum, StringComparison.Ordinal))
                return MigrationOutcome.AlreadyApplied; // al toegepast, checksum klopt — no-op

            // #1112: is de oude ledger-waarde een rauwe checksum van exact dit bestand met andere
            // regeleindes? Dan is er inhoudelijk niets gewijzigd — alleen de ledger-rij bijwerken.
            if (IsLineEndingVariant(existing, content))
            {
                await UpdateAppliedChecksumAsync(connection, fileName, checksum, ct);
                return MigrationOutcome.ChecksumNormalized;
            }

            throw new InvalidOperationException(
                $"Migratie '{fileName}' is al toegepast met een andere checksum. " +
                "Een reeds toegepast migratiebestand mag nooit achteraf gewijzigd worden — " +
                "voeg een nieuw, opvolgend migratiebestand toe in plaats daarvan.");
        }

        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = new NpgsqlCommand(content, connection, tx))
                await cmd.ExecuteNonQueryAsync(ct);

            await using var record = new NpgsqlCommand(
                "INSERT INTO schema_migrations (filename, checksum, applied_at) VALUES (@f, @c, NOW())",
                connection, tx);
            record.Parameters.AddWithValue("f", fileName);
            record.Parameters.AddWithValue("c", checksum);
            await record.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
            return MigrationOutcome.Applied;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// #1098: de namen van alle migratiebestanden die in deze assembly zijn ingesloten
    /// (<c>Database.Postgres.csproj</c>, <c>EmbeddedResource</c>), in toepassingsvolgorde.
    /// Bedoeld voor <c>/api/health</c>: de Function App heeft geen migratiemap op schijf, maar moet
    /// wél kunnen melden welke migraties de database nog mist. De map blijft de enige bron van de
    /// migraties zelf — <see cref="RunAsync"/> leest nog steeds van schijf.
    /// </summary>
    public static IReadOnlyList<string> BundledMigrationNames { get; } = typeof(MigrationRunner).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(BundledResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
        .Select(n => n[BundledResourcePrefix.Length..])
        .OrderBy(ExtractSequenceNumber)
        .ThenBy(n => n, StringComparer.Ordinal)
        .ToList();

    private const string BundledResourcePrefix = "migrations/";

    /// <summary>
    /// #1098: welke van de meegeleverde migraties staan nog niet in de ledger <c>schema_migrations</c>?
    /// Leeg = schema en code lopen gelijk. Bestaat de ledger-tabel niet (<c>42P01</c>), dan is er nog
    /// nooit gemigreerd en is álles nog toe te passen. Leest alleen; past nooit iets toe — een
    /// migratie tegen productie is en blijft een bewuste handeling van de eigenaar
    /// (ARCHITECTUUR-DATABASE-TIERS.md §49, §55).
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT filename FROM schema_migrations", connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                applied.Add(reader.GetString(0));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Geen ledger → geen enkele migratie toegepast.
        }

        return ComputePending(BundledMigrationNames, applied);
    }

    /// <summary>De beslisregel zelf, los van database en assembly zodat hij toetsbaar is.</summary>
    public static IReadOnlyList<string> ComputePending(IEnumerable<string> bundled, IReadOnlySet<string> applied)
        => bundled.Where(n => !applied.Contains(n)).ToList();

    public static int ExtractSequenceNumber(string fileName)
    {
        var digits = new string(fileName.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0)
            throw new InvalidOperationException(
                $"Migratiebestand '{fileName}' mist een numeriek volgnummer-voorvoegsel (bijv. '001_baseline.sql').");
        return int.Parse(digits);
    }

    /// <summary>
    /// SHA-256 over de inhoud met genormaliseerde regeleindes (<c>\r\n</c> → <c>\n</c>), zodat
    /// dezelfde migratie op Windows (CRLF-werkmap) en macOS/Linux/CI (LF) dezelfde waarde geeft
    /// (#1112). Een bestand dat uitsluitend LF bevat — de vorm waarin git ze bewaart — krijgt
    /// exact dezelfde checksum als vóór #1112, dus bestaande, op LF gevulde ledgers blijven kloppen.
    /// </summary>
    public static string ComputeChecksum(string content)
        => ComputeRawChecksum(NormalizeLineEndings(content));

    public static string NormalizeLineEndings(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Is <paramref name="storedChecksum"/> de rauwe (niet-genormaliseerde) checksum van
    /// <paramref name="content"/> in een andere regeleinde-vorm? Getoetst worden de CRLF-variant
    /// van de genormaliseerde inhoud (ledger gevuld vanaf een Windows-checkout) en de inhoud zoals
    /// hij nu op schijf staat (ledger gevuld vanaf een LF-checkout terwijl de huidige werkmap CRLF
    /// heeft). <c>true</c> betekent: bewezen hetzelfde bestand, alleen andere regeleindes — geen
    /// inhoudelijke wijziging. Elke andere afwijking geeft <c>false</c> en blijft een harde fout.
    /// </summary>
    public static bool IsLineEndingVariant(string storedChecksum, string content)
    {
        var lf = NormalizeLineEndings(content);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        return string.Equals(storedChecksum, ComputeRawChecksum(crlf), StringComparison.Ordinal)
            || string.Equals(storedChecksum, ComputeRawChecksum(content), StringComparison.Ordinal);
    }

    private static string ComputeRawChecksum(string content)
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

    private static async Task UpdateAppliedChecksumAsync(NpgsqlConnection connection, string fileName, string checksum, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE schema_migrations SET checksum = @c WHERE filename = @f", connection);
        cmd.Parameters.AddWithValue("f", fileName);
        cmd.Parameters.AddWithValue("c", checksum);
        await cmd.ExecuteNonQueryAsync(ct);
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

/// <summary>
/// Uitkomst van één <see cref="MigrationRunner.RunAsync"/> (#1112): welke bestanden nieuw zijn
/// toegepast, welke al stonden, en bij welke de ledger-checksum is omgeschreven van een rauwe
/// CRLF-waarde naar de genormaliseerde waarde. De CLI rapporteert dit zodat een reparatie in een
/// deploy-log of handmatige ronde zichtbaar is en niet stil gebeurt.
/// </summary>
public sealed class MigrationRunResult
{
    public List<string> Applied { get; } = new();
    public List<string> AlreadyApplied { get; } = new();
    public List<string> ChecksumNormalized { get; } = new();
}
