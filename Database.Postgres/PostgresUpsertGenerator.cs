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
    /// <para>
    /// <b>Sportlink is leidend (#1547).</b> Een rij die opnieuw in stg verschijnt, wordt altijd
    /// hersteld (<c>mta_deleted = NULL</c>), ook zonder inhoudelijke wijziging. Dat herstel telt als
    /// wijziging: <c>mta_modified</c> schuift dan één keer op. Een actieve, ongewijzigde rij blijft
    /// onaangeroerd, zoals hierboven.
    /// </para>
    /// </summary>
    public static string GenerateUpsertFromStgToHis(EntityDefinition entity)
    {
        var table = PostgresIdentifier.Quote(entity.EntityName);
        var bkColumn = PostgresIdentifier.Quote(PostgresSchemaGenerator.BusinessKeyColumnName(entity));
        var dataColumnNames = entity.Columns.Select(c => c.Name).ToList();
        var quotedDataColumns = string.Join(", ", dataColumnNames.Select(PostgresIdentifier.Quote));
        var mtaInserted = PostgresIdentifier.Quote("mta_inserted");
        var mtaModified = PostgresIdentifier.Quote("mta_modified");

        var mtaDeleted = PostgresIdentifier.Quote("mta_deleted");

        var setClauses = string.Join(",\n    ", dataColumnNames.Select(c =>
            $"{PostgresIdentifier.Quote(c)} = EXCLUDED.{PostgresIdentifier.Quote(c)}"));
        setClauses += $",\n    {mtaModified} = NOW()";
        setClauses += $",\n    {mtaDeleted} = NULL";

        // #1547: een rij die in stg staat, bestaat bij Sportlink — ook als een eerdere run hem als
        // verwijderd markeerde. Zonder deze twee aanvullingen bleef zo'n rij voor altijd verborgen:
        // de update zette mta_deleted nooit terug, en bij ongewijzigde data liep hij niet eens.
        var changeDetection = string.Join(" OR ", dataColumnNames.Select(c =>
            $"his.{table}.{PostgresIdentifier.Quote(c)} IS DISTINCT FROM EXCLUDED.{PostgresIdentifier.Quote(c)}"));
        changeDetection += $" OR his.{table}.{mtaDeleted} IS NOT NULL";

        return
            $"INSERT INTO his.{table} ({quotedDataColumns}, {mtaInserted}, {mtaModified})\n" +
            $"SELECT {quotedDataColumns}, NOW(), NOW()\n" +
            $"FROM stg.{table}\n" +
            $"ON CONFLICT ({bkColumn}) DO UPDATE SET\n" +
            $"    {setClauses}\n" +
            $"WHERE {changeDetection};\n";
    }
}
