namespace Database.Postgres;

/// <summary>
/// Genereert het upsert-/change-detection-statement (#818): Postgres' functionele
/// MERGE-equivalent, <c>INSERT ... ON CONFLICT ... DO UPDATE ... WHERE IS DISTINCT FROM</c>, met
/// de synthetische business-key-kolom (zie <see cref="PostgresSchemaGenerator"/>) als
/// conflict-target. <c>ON CONFLICT</c> vereist een echte unique constraint/index op precies de
/// conflict-target-kolom — die legt <see cref="PostgresSchemaGenerator.GenerateHisTable"/> al aan.
/// </summary>
public static class PostgresUpsertGenerator
{
    /// <summary>
    /// Bouwt de set-based upsert van stg naar his voor deze entiteit. De
    /// <c>WHERE ... IS DISTINCT FROM</c>-clausule dekt uitsluitend de data-/businesskolommen —
    /// nooit de audit-kolommen — zodat <c>mta_modified</c> alleen bijgewerkt wordt bij een
    /// daadwerkelijke inhoudelijke wijziging (de bestaande audit-semantiek, niet stilzwijgend
    /// laten verwateren bij de tier-migratie).
    /// </summary>
    public static string GenerateUpsertFromStgToHis(EntityDefinition entity)
    {
        var table = PostgresIdentifier.Quote(entity.EntityName);
        var bkColumn = PostgresIdentifier.Quote(PostgresSchemaGenerator.BusinessKeyColumnName(entity));
        var dataColumnNames = entity.Columns.Select(c => c.Name).ToList();
        var quotedDataColumns = string.Join(", ", dataColumnNames.Select(PostgresIdentifier.Quote));
        var mtaInserted = PostgresIdentifier.Quote("mta_inserted");
        var mtaModified = PostgresIdentifier.Quote("mta_modified");

        var setClauses = string.Join(",\n    ", dataColumnNames.Select(c =>
            $"{PostgresIdentifier.Quote(c)} = EXCLUDED.{PostgresIdentifier.Quote(c)}"));
        setClauses += $",\n    {mtaModified} = NOW()";

        var changeDetection = string.Join(" OR ", dataColumnNames.Select(c =>
            $"his.{table}.{PostgresIdentifier.Quote(c)} IS DISTINCT FROM EXCLUDED.{PostgresIdentifier.Quote(c)}"));

        return
            $"INSERT INTO his.{table} ({quotedDataColumns}, {mtaInserted}, {mtaModified})\n" +
            $"SELECT {quotedDataColumns}, NOW(), NOW()\n" +
            $"FROM stg.{table}\n" +
            $"ON CONFLICT ({bkColumn}) DO UPDATE SET\n" +
            $"    {setClauses}\n" +
            $"WHERE {changeDetection};\n";
    }
}
