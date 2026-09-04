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
/// <para>
/// <b>#976-fix, ontdekt tijdens een lokale end-to-end-test tegen democlub-data:</b> een eerdere
/// versie probeerde elke Postgres-identity-kolom (16 van de 19 doeltabellen hebben er een) met
/// zijn originele SQL-Server-waarde te vullen (<c>OVERRIDING SYSTEM VALUE</c>). Dat botste
/// meteen: <c>veldbeschikbaarheid</c> begint in Postgres al bij <c>id=1</c> vanuit de
/// AllStars-democlubseed, en de productieclub heeft in SQL Server zijn eigen, onafhankelijke
/// IDENTITY-reeks die net zo goed bij 1 begint — een botsing die zich bij elke identity-kolom kan
/// voordoen, niet alleen hier. IDs zijn surrogaatsleutels zonder betekenis buiten de database (in
/// tegenstelling tot bijv. <c>veldnummer</c>, een natuurlijke sleutel die AllStars bewust in een
/// gereserveerd bereik 101+ houdt om dit te vermijden) — de juiste aanpak is dus: laat Postgres
/// altijd zelf een nieuwe waarde genereren, en volg voor de kolommen die daadwerkelijk als foreign
/// key vanuit een andere gekopieerde tabel worden gebruikt (<see cref="IdentityMapKey"/> +
/// <see cref="ForeignKeyRemaps"/>) de nieuwe waarde in plaats van de oude te kopiëren.
/// </para>
/// </summary>
public sealed record TableMapping(
    string SourceSchema, string SourceTable,
    string TargetSchema, string TargetTable,
    bool HasClubCode,
    /// <summary>
    /// Niet-null als andere tabellen in deze lijst een foreign key naar de identity-kolom van
    /// déze tabel hebben — na de kopie is de mapping (oude SQL-Server-id -> nieuwe Postgres-id)
    /// dan opvraagbaar via <see cref="IdMapRegistry"/> onder deze sleutel. Vereist dat de tabel
    /// precies één identity-kolom heeft.
    /// </summary>
    string? IdentityMapKey = null,
    /// <summary>
    /// Doelkolomnaam -> sleutel in <see cref="IdMapRegistry"/> waarmee de gekopieerde waarde van
    /// die kolom vertaald moet worden. De tabel waarnaar verwezen wordt (via
    /// <see cref="IdentityMapKey"/>) moet dus al eerder in de kopieervolgorde staan. Een NULL
    /// bronwaarde blijft NULL (nooit remappen naar een verzonnen waarde).
    /// </summary>
    IReadOnlyDictionary<string, string>? ForeignKeyRemaps = null);

public sealed record TableCopyResult(
    string TargetTable, int SourceRowCount, int CopiedRowCount, bool RowCountsMatch);

/// <summary>Eén doelkolom: naam en of Postgres 'm als <c>GENERATED ALWAYS AS IDENTITY</c> beheert.</summary>
public sealed record ColumnInfo(string Name, bool IsIdentity);

/// <summary>
/// Houdt per <see cref="TableMapping.IdentityMapKey"/> de mapping (oude SQL-Server-id -> nieuwe
/// Postgres-id) bij, zodat een later gekopieerde tabel zijn foreign-key-kolom kan vertalen. Eén
/// registry-instantie per volledige kopieerrun (alle tabellen delen 'm).
/// </summary>
public sealed class IdMapRegistry
{
    private readonly Dictionary<string, Dictionary<long, long>> _maps = new();

    public void Record(string key, long oldId, long newId)
        => (_maps.TryGetValue(key, out var map) ? map : _maps[key] = new()).Add(oldId, newId);

    public long Translate(string key, long oldId)
    {
        if (!_maps.TryGetValue(key, out var map) || !map.TryGetValue(oldId, out var newId))
            throw new InvalidOperationException(
                $"Geen nieuwe id gevonden voor '{key}'-waarde {oldId} — staat de brontabel voor '{key}' " +
                "eerder in de kopieervolgorde, en bestond deze rij daadwerkelijk in de bron?");
        return newId;
    }
}

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
        string productionClubCode, bool dryRun, IdMapRegistry idMaps, TextWriter log, CancellationToken ct)
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
            var copied = mapping.IdentityMapKey is not null
                ? await StreamCopyWithIdCaptureAsync(source, target, tx, mapping, columns, productionClubCode, idMaps, ct)
                : await StreamCopyAsync(source, target, tx, mapping, columns, productionClubCode, idMaps, ct);
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

    private static async Task<List<ColumnInfo>> GetTargetColumnsAsync(
        NpgsqlConnection target, string schema, string table, CancellationToken ct)
    {
        var columns = new List<ColumnInfo>();
        await using var cmd = new NpgsqlCommand("""
            SELECT column_name, is_identity = 'YES'
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
              AND is_generated = 'NEVER'
            ORDER BY ordinal_position
            """, target);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(new ColumnInfo(reader.GetString(0), reader.GetBoolean(1)));
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

    /// <summary>
    /// Pad voor de meeste tabellen: identity-kolommen worden nooit gekopieerd (Postgres genereert
    /// zelf een nieuwe waarde) — behalve als de kolom in <see cref="TableMapping.ForeignKeyRemaps"/>
    /// staat, dan wordt de brontabelwaarde via <see cref="IdMapRegistry"/> vertaald naar de al
    /// gegenereerde nieuwe waarde van de tabel waar hij naar verwijst.
    /// </summary>
    private static async Task<int> StreamCopyAsync(
        SqlConnection source, NpgsqlConnection target, NpgsqlTransaction tx, TableMapping mapping,
        List<ColumnInfo> columns, string productionClubCode, IdMapRegistry idMaps, CancellationToken ct)
    {
        var insertColumns = columns.Where(c => !c.IsIdentity).ToList();
        var selectColumns = insertColumns.Select(c => c.Name).ToList();

        await using var reader = await OpenSourceReaderAsync(source, mapping, selectColumns, productionClubCode, ct);

        var quotedColumns = string.Join(", ", insertColumns.Select(c => $"\"{c.Name}\""));
        var parameterNames = string.Join(", ", Enumerable.Range(0, insertColumns.Count).Select(i => $"${i + 1}"));
        var insertSql = $"INSERT INTO {mapping.TargetSchema}.{mapping.TargetTable} ({quotedColumns}) VALUES ({parameterNames})";

        var copied = 0;
        const int batchSize = 500;
        var batch = new NpgsqlBatch(target, tx);

        while (await reader.ReadAsync(ct))
        {
            var batchCommand = new NpgsqlBatchCommand(insertSql);
            for (var i = 0; i < insertColumns.Count; i++)
            {
                var value = ResolveValue(mapping, insertColumns[i].Name, reader.GetValue(i), idMaps);
                batchCommand.Parameters.AddWithValue(value);
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

    /// <summary>
    /// Pad voor een tabel met <see cref="TableMapping.IdentityMapKey"/> gezet: leest ook de
    /// brontabel-identity-kolom mee (nodig als sleutel voor de mapping, maar wordt zelf NOOIT
    /// geïnsert), voegt rij-voor-rij toe met <c>RETURNING</c> om de nieuw gegenereerde Postgres-id
    /// te achterhalen, en registreert (oude id -> nieuwe id) in <see cref="IdMapRegistry"/> zodat
    /// later gekopieerde tabellen hun foreign key ernaar kunnen vertalen. Geen batch-insert hier —
    /// deze tabellen zijn qua rijaantal klein (configuratietabellen), en RETURNING per rij is
    /// eenvoudiger correct te houden dan een batch met per-commando resultaten.
    /// </summary>
    private static async Task<int> StreamCopyWithIdCaptureAsync(
        SqlConnection source, NpgsqlConnection target, NpgsqlTransaction tx, TableMapping mapping,
        List<ColumnInfo> columns, string productionClubCode, IdMapRegistry idMaps, CancellationToken ct)
    {
        var identityColumn = columns.SingleOrDefault(c => c.IsIdentity)
            ?? throw new InvalidOperationException(
                $"{mapping.TargetTable}: IdentityMapKey is gezet maar de tabel heeft geen (of meer dan één) identity-kolom.");
        var insertColumns = columns.Where(c => !c.IsIdentity).ToList();
        // De identity-kolom staat als LAATSTE in de select — nodig als mapping-sleutel, nooit geïnsert.
        var selectColumns = insertColumns.Select(c => c.Name).Append(identityColumn.Name).ToList();

        await using var reader = await OpenSourceReaderAsync(source, mapping, selectColumns, productionClubCode, ct);

        var quotedColumns = string.Join(", ", insertColumns.Select(c => $"\"{c.Name}\""));
        var parameterNames = string.Join(", ", Enumerable.Range(0, insertColumns.Count).Select(i => $"${i + 1}"));
        var insertSql =
            $"INSERT INTO {mapping.TargetSchema}.{mapping.TargetTable} ({quotedColumns}) VALUES ({parameterNames}) " +
            $"RETURNING \"{identityColumn.Name}\"";

        var copied = 0;
        var oldIdOrdinal = insertColumns.Count;
        while (await reader.ReadAsync(ct))
        {
            var oldId = Convert.ToInt64(reader.GetValue(oldIdOrdinal));

            await using var insertCmd = new NpgsqlCommand(insertSql, target, tx);
            for (var i = 0; i < insertColumns.Count; i++)
            {
                var value = ResolveValue(mapping, insertColumns[i].Name, reader.GetValue(i), idMaps);
                insertCmd.Parameters.AddWithValue(value);
            }
            var newId = Convert.ToInt64((await insertCmd.ExecuteScalarAsync(ct))!);
            idMaps.Record(mapping.IdentityMapKey!, oldId, newId);
            copied++;
        }

        return copied;
    }

    private static async Task<SqlDataReader> OpenSourceReaderAsync(
        SqlConnection source, TableMapping mapping, List<string> selectColumns, string productionClubCode, CancellationToken ct)
    {
        var columnList = string.Join(", ", selectColumns.Select(c => $"[{c}]"));
        var whereClause = mapping.HasClubCode ? "WHERE [ClubCode] = @clubCode" : "";
        var selectCmd = new SqlCommand(
            $"SELECT {columnList} FROM [{mapping.SourceSchema}].[{mapping.SourceTable}] {whereClause}", source);
        if (mapping.HasClubCode)
            selectCmd.Parameters.AddWithValue("@clubCode", productionClubCode);
        return await selectCmd.ExecuteReaderAsync(ct);
    }

    private static object ResolveValue(TableMapping mapping, string columnName, object rawValue, IdMapRegistry idMaps)
    {
        if (rawValue is DBNull)
            return DBNull.Value;
        if (mapping.ForeignKeyRemaps is null || !mapping.ForeignKeyRemaps.TryGetValue(columnName, out var mapKey))
            return rawValue;
        return idMaps.Translate(mapKey, Convert.ToInt64(rawValue));
    }
}
