using Microsoft.Data.SqlClient;
using Npgsql;

namespace SqlServerToPostgresCopy;

/// <summary>
/// Eén tabel die van SQL Server naar Postgres wordt overgezet: brontabel (SQL Server, PascalCase-
/// identifiers), doeltabel (Postgres, lowercase — conform docs/ARCHITECTUUR-DATABASE-TIERS.md §3),
/// en of de tabel een <c>ClubCode</c>-kolom heeft.
/// <para>
/// <b>Waarom ClubCode ertoe doet:</b> de Postgres-doeldatabase bevat al AllStars FC-democlubdata
/// (migraties 006/010). Een kale <c>DELETE FROM</c> zonder ClubCode-filter zou die democlubrijen
/// mee wissen. Voor elke tabel met een ClubCode-kolom scopen zowel de delete als de select naar
/// exact de productieclub — democlubrijen blijven met rust, precies zoals
/// <c>Database.Postgres/TeambegeleidingImporter.cs</c> dat al doet voor <c>avg.teambegeleiding</c>.
/// </para>
/// </summary>
public sealed record TableMapping(
    string SourceSchema, string SourceTable,
    string TargetSchema, string TargetTable,
    bool HasClubCode);

public sealed record TableCopyResult(
    string TargetTable, int SourceRowCount, int CopiedRowCount, bool RowCountsMatch);

/// <summary>
/// Generieke, kolomonafhankelijke rijkopieerder SQL Server -> Postgres voor #976 (eenmalige
/// productiecutover). Leest de kolomlijst van de Postgres-doeltabel uit diens eigen
/// <c>information_schema</c> — dat is de bron van waarheid, niet een handmatig bijgehouden mapping
/// per tabel — en selecteert exact diezelfde kolomnamen bij de bron. Dat werkt omdat de
/// Postgres-kolomnamen bewust de lowercase-vorm zijn van de identieke SQL Server-naam (nooit een
/// hernoeming, zie KnownEntities.cs) en SQL Server identifiers standaard case-insensitive
/// resolveert.
/// </summary>
public static class TableCopier
{
    public static async Task<TableCopyResult> CopyAsync(
        SqlConnection source, NpgsqlConnection target, TableMapping mapping,
        string productionClubCode, bool dryRun, TextWriter log, CancellationToken ct)
    {
        var columns = await GetTargetColumnsAsync(target, mapping.TargetSchema, mapping.TargetTable, ct);
        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"Geen kolommen gevonden voor doeltabel {mapping.TargetSchema}.{mapping.TargetTable} " +
                "— bestaat de tabel? Migraties toegepast?");

        var sourceCount = await CountSourceRowsAsync(source, mapping, productionClubCode, ct);
        log.WriteLine($"  bron {mapping.SourceSchema}.{mapping.SourceTable}: {sourceCount} rij(en) voor club '{productionClubCode}'");

        if (dryRun)
            return new TableCopyResult(mapping.TargetTable, sourceCount, 0, RowCountsMatch: false);

        await using var tx = await target.BeginTransactionAsync(ct);
        try
        {
            await DeleteExistingAsync(target, tx, mapping, productionClubCode, ct);
            var copied = await StreamCopyAsync(source, target, tx, mapping, columns, productionClubCode, ct);
            await tx.CommitAsync(ct);

            log.WriteLine($"  doel  {mapping.TargetSchema}.{mapping.TargetTable}: {copied} rij(en) gekopieerd");
            return new TableCopyResult(mapping.TargetTable, sourceCount, copied, RowCountsMatch: sourceCount == copied);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task<List<string>> GetTargetColumnsAsync(
        NpgsqlConnection target, string schema, string table, CancellationToken ct)
    {
        var columns = new List<string>();
        await using var cmd = new NpgsqlCommand("""
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
              AND is_generated = 'NEVER'
            ORDER BY ordinal_position
            """, target);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<int> CountSourceRowsAsync(
        SqlConnection source, TableMapping mapping, string productionClubCode, CancellationToken ct)
    {
        var whereClause = mapping.HasClubCode ? "WHERE [ClubCode] = @clubCode" : "";
        await using var cmd = new SqlCommand(
            $"SELECT COUNT(*) FROM [{mapping.SourceSchema}].[{mapping.SourceTable}] {whereClause}", source);
        if (mapping.HasClubCode)
            cmd.Parameters.AddWithValue("@clubCode", productionClubCode);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task DeleteExistingAsync(
        NpgsqlConnection target, NpgsqlTransaction tx, TableMapping mapping, string productionClubCode, CancellationToken ct)
    {
        // Nooit TRUNCATE — dat zou ook AllStars FC-democlubrijen wissen. DELETE (zonder ClubCode-
        // filter als de tabel er geen heeft) maakt deze kopieerstap herhaalbaar zonder duplicaten
        // bij een herstart van de migratie.
        var whereClause = mapping.HasClubCode ? "WHERE clubcode = @clubCode" : "";
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {mapping.TargetSchema}.{mapping.TargetTable} {whereClause}", target, tx);
        if (mapping.HasClubCode)
            cmd.Parameters.AddWithValue("clubCode", productionClubCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> StreamCopyAsync(
        SqlConnection source, NpgsqlConnection target, NpgsqlTransaction tx, TableMapping mapping,
        List<string> columns, string productionClubCode, CancellationToken ct)
    {
        var columnList = string.Join(", ", columns.Select(c => $"[{c}]"));
        var whereClause = mapping.HasClubCode ? "WHERE [ClubCode] = @clubCode" : "";
        await using var selectCmd = new SqlCommand(
            $"SELECT {columnList} FROM [{mapping.SourceSchema}].[{mapping.SourceTable}] {whereClause}", source);
        if (mapping.HasClubCode)
            selectCmd.Parameters.AddWithValue("@clubCode", productionClubCode);

        await using var reader = await selectCmd.ExecuteReaderAsync(ct);

        var quotedColumns = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var parameterNames = string.Join(", ", Enumerable.Range(0, columns.Count).Select(i => $"${i + 1}"));
        var insertSql = $"INSERT INTO {mapping.TargetSchema}.{mapping.TargetTable} ({quotedColumns}) VALUES ({parameterNames})";

        var copied = 0;
        const int batchSize = 500;
        var batch = new NpgsqlBatch(target, tx);

        while (await reader.ReadAsync(ct))
        {
            var batchCommand = new NpgsqlBatchCommand(insertSql);
            for (var i = 0; i < columns.Count; i++)
            {
                var value = reader.GetValue(i);
                batchCommand.Parameters.AddWithValue(value is DBNull ? DBNull.Value : value);
            }
            batch.BatchCommands.Add(batchCommand);
            copied++;

            if (batch.BatchCommands.Count >= batchSize)
            {
                await batch.ExecuteNonQueryAsync(ct);
                batch = new NpgsqlBatch(target, tx);
            }
        }

        if (batch.BatchCommands.Count > 0)
            await batch.ExecuteNonQueryAsync(ct);

        return copied;
    }
}
