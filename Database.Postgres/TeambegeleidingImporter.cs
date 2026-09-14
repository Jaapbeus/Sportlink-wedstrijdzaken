using NpgsqlTypes;
using Npgsql;

namespace Database.Postgres;

/// <summary>Eén rij uit de teambegeleiding-CSV, al genormaliseerd naar de canonieke kolomnamen.</summary>
public sealed record TeambegeleidingRow(
    string? Team,
    string? LeeftijdscategorieTeam,
    string? Teamrol,
    string? Naam,
    string? Emailadres,
    string? Telefoonnummer);

public sealed record TeambegeleidingImportResult(int AantalRijen, long DuurMs);

/// <summary>
/// Postgres-equivalent van <c>exports/import-teambegeleiding-to-sql.ps1</c> (#824) — het
/// AVG-gevoelige database-interactiedeel (delete-vóór-insert, bulklaad, auditlog, staleness-check).
/// De flexibele CSV-kolomherkenning (aliassen, samengestelde Naam/Telefoonnummer-velden) van het
/// origineel is hier bewust NIET herbouwd — deze klasse accepteert al genormaliseerde
/// <see cref="TeambegeleidingRow"/>-rijen. Zie de PR-beschrijving voor de scope-afbakening.
/// <para>
/// <b>Drie gedragswijzigingen t.o.v. het SQL Server-origineel</b>, alle drie expliciet geëist door
/// de #824-review-fact-check-addendum (geen "gedrag ongewijzigd"-poort, want het origineel had hier
/// aantoonbare gebreken):
/// </para>
/// <list type="number">
/// <item><description><b>Atomisch:</b> delete + bulklaad + auditlog-insert lopen in één
/// transactie. Het origineel deed drie losse, niet-getransactioneerde aanroepen — een crash
/// tussen de delete en de bulk copy liet de club zonder data achter, zonder foutmelding in
/// <c>avg.ImportLog</c>.</description></item>
/// <item><description><b>ClubCode-gevalideerd via SyncEnabled:</b> een impliciete club-keuze
/// valideert expliciet <c>syncenabled = true</c> (patroon uit <c>Utilities.cs</c>/
/// <c>AdminTestDataFunction.cs</c>), zodat de AllStars FC-democlub nooit per ongeluk als doelclub
/// voor échte persoonsgegevens wordt geselecteerd.</description></item>
/// <item><description><b>ClubCode-gescoped staleness-check:</b> <c>MIN(mta_imported)</c> filtert nu
/// op de specifieke club — het origineel keek over alle clubs heen, dus een verse import voor club
/// A verborg stale data van club B.</description></item>
/// </list>
/// </summary>
public static class TeambegeleidingImporter
{
    /// <summary>
    /// Bepaalt de doelclub: expliciet meegegeven, of anders de enige actieve
    /// (<c>syncenabled = true</c>) club — nooit een kale <c>SELECT TOP 1</c> zonder die validatie.
    /// </summary>
    public static async Task<string> ResolveClubCodeAsync(
        NpgsqlConnection connection, string? explicitClubCode, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitClubCode))
            return explicitClubCode;

        await using var cmd = new NpgsqlCommand(
            "SELECT clubcode FROM public.appsettings WHERE syncenabled = true ORDER BY clubcode LIMIT 1",
            connection);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not string clubCode || string.IsNullOrWhiteSpace(clubCode))
            throw new InvalidOperationException(
                "Geen actieve club gevonden (syncenabled = true in public.appsettings) — " +
                "geef -ClubCode expliciet mee.");
        return clubCode;
    }

    /// <summary>
    /// Verwijdert bestaande rijen voor <paramref name="clubCode"/>, laadt de nieuwe rijen via
    /// Postgres' native binaire COPY-protocol, en schrijft de auditrij — alles in één transactie.
    /// Nooit een Postgres-equivalent van <c>TRUNCATE</c>: dat zou andere clubs' rijen ook wissen.
    /// <para>
    /// <b>#1132:</b> vóór de DELETE wordt een <c>pg_advisory_xact_lock</c> op een per-club sleutel
    /// genomen. Zonder die serialisatie kunnen twee overlappende imports voor dezelfde club onder
    /// Read Committed allebei committen: import B's DELETE ziet de rijen van import A (die na B's
    /// DELETE-snapshot zijn ingevoegd) niet, en de tabel houdt de vereniging van beide batches over
    /// in plaats van precies één complete vervanging. De lock is transactie-gebonden (niet
    /// sessie-gebonden zoals in <see cref="MigrationRunner"/>) en geeft zichzelf dus automatisch
    /// vrij bij commit of rollback — geen aparte release-aanroep nodig.
    /// </para>
    /// </summary>
    public static async Task<TeambegeleidingImportResult> ImportAsync(
        NpgsqlConnection connection, string clubCode, IReadOnlyList<TeambegeleidingRow> rows,
        string? csvBestand, string? importerendeDoor, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await AcquireClubLockAsync(connection, tx, clubCode, ct);
            await DeleteBestaandeRijenAsync(connection, tx, clubCode, ct);
            await KopieerRijenAsync(connection, clubCode, rows, ct);
            var duurMs = (int)stopwatch.ElapsedMilliseconds;
            await SchrijfAuditLogAsync(connection, tx, clubCode, rows.Count, csvBestand, importerendeDoor, duurMs, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        stopwatch.Stop();
        return new TeambegeleidingImportResult(rows.Count, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Serialiseert vervangingen per club (#1132): een tweede <see cref="ImportAsync"/>-aanroep
    /// voor dezelfde club wacht hier tot de eerste transactie commit of rollbackt, en ziet
    /// daarna diens rijen — ook als de tabel voor die club leeg was. <c>hashtext</c> zet de
    /// sleutel om naar een stabiele <c>int</c>; <c>pg_advisory_xact_lock</c> vereist een
    /// <c>bigint</c>, vandaar de cast.
    /// </summary>
    private static async Task AcquireClubLockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string clubCode, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('teambegeleiding:' || @cc)::bigint)", connection, tx);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteBestaandeRijenAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string clubCode, CancellationToken ct)
    {
        await using var delete = new NpgsqlCommand(
            "DELETE FROM avg.teambegeleiding WHERE clubcode = @cc", connection, tx);
        delete.Parameters.AddWithValue("cc", clubCode);
        await delete.ExecuteNonQueryAsync(ct);
    }

    private static async Task KopieerRijenAsync(NpgsqlConnection connection, string clubCode, IReadOnlyList<TeambegeleidingRow> rows, CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY avg.teambegeleiding (team, leeftijdscategorieteam, teamrol, naam, emailadres, telefoonnummer, clubcode) FROM STDIN (FORMAT BINARY)",
            ct);

        foreach (var row in rows)
        {
            await writer.StartRowAsync(ct);
            await WriteNullableAsync(writer, row.Team, ct);
            await WriteNullableAsync(writer, row.LeeftijdscategorieTeam, ct);
            await WriteNullableAsync(writer, row.Teamrol, ct);
            await WriteNullableAsync(writer, row.Naam, ct);
            await WriteNullableAsync(writer, row.Emailadres, ct);
            await WriteNullableAsync(writer, row.Telefoonnummer, ct);
            await writer.WriteAsync(clubCode, NpgsqlDbType.Varchar, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private static async Task SchrijfAuditLogAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string clubCode, int aantalRijen, string? csvBestand, string? importerendeDoor, long duurMs, CancellationToken ct)
    {
        await using var log = new NpgsqlCommand("""
            INSERT INTO avg.importlog (aantalrijen, csvbestand, importerendedoor, duur_ms, clubcode)
            VALUES (@rijen, @csv, @door, @duur, @club)
            """, connection, tx);
        log.Parameters.AddWithValue("rijen", aantalRijen);
        log.Parameters.AddWithValue("csv", (object?)csvBestand ?? DBNull.Value);
        log.Parameters.AddWithValue("door", (object?)importerendeDoor ?? DBNull.Value);
        log.Parameters.AddWithValue("duur", (int)duurMs);
        log.Parameters.AddWithValue("club", clubCode);
        await log.ExecuteNonQueryAsync(ct);
    }

    private static Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
        => string.IsNullOrWhiteSpace(value)
            ? writer.WriteNullAsync(ct)
            : writer.WriteAsync(value, NpgsqlDbType.Varchar, ct);

    /// <summary>
    /// AVG #208-staleness-check, ClubCode-gescoped (het origineel keek over alle clubs heen).
    /// </summary>
    public static async Task<int?> GetOudsteImportLeeftijdInDagenAsync(
        NpgsqlConnection connection, string clubCode, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT MIN(mta_imported) FROM avg.teambegeleiding WHERE clubcode = @cc", connection);
        cmd.Parameters.AddWithValue("cc", clubCode);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not DateTime oudsteImport)
            return null;

        return (int)(DateTime.UtcNow - DateTime.SpecifyKind(oudsteImport, DateTimeKind.Utc)).TotalDays;
    }
}
