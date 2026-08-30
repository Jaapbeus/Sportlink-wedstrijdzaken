using System.Text;

namespace Database.Postgres;

/// <summary>
/// Genereert Postgres-DDL voor de stg- en his-tabel van een entiteit uit één
/// <see cref="EntityDefinition"/> (#818) — build-time/design-time stringgeneratie, geen
/// afhankelijkheid van Postgres' eigen systeemcatalogus tijdens runtime (in tegenstelling tot het
/// SQL-Server-patroon, waar sp_CreateTargetTableFromSource sys.* introspecteert tijdens executie).
/// </summary>
public static class PostgresSchemaGenerator
{
    /// <summary>
    /// Scheidingsteken tussen de gecoalesceerde business-key-kolommen in de synthetische
    /// bk_-kolom — een controlekarakter dat in praktijk nooit in een echte kolomwaarde voorkomt,
    /// zodat twee verschillende combinaties van kolomwaarden nooit toevallig tot dezelfde
    /// samengevoegde string vouwen (bijv. ("ab","c") vs. ("a","bc") zonder scheidingsteken).
    /// </summary>
    internal const string BusinessKeySeparator = "\u0001";

    /// <summary>Naam van de gegenereerde, nooit-NULL synthetische business-key-kolom.</summary>
    public static string BusinessKeyColumnName(EntityDefinition entity) => $"bk_{entity.EntityName}";

    /// <summary>
    /// DDL voor de stg-tabel: DROP + CREATE, exact de aangeleverde data-kolommen, geen audit,
    /// geen surrogate-sleutel, geen unieke constraint — analoog aan FunctionApp/CreateTable.cs'
    /// bestaande DROP-TABLE-IF-EXISTS-patroon voor SQL Server.
    /// </summary>
    public static string GenerateStgTable(EntityDefinition entity)
    {
        var table = PostgresIdentifier.Quote(entity.EntityName);
        var sb = new StringBuilder();
        sb.AppendLine($"DROP TABLE IF EXISTS stg.{table};");
        sb.AppendLine($"CREATE TABLE stg.{table} (");
        AppendColumnLines(sb, entity.Columns, forceTrailingComma: false);
        sb.AppendLine(");");
        return sb.ToString();
    }

    /// <summary>
    /// DDL voor de his-tabel: idempotent (<c>CREATE TABLE IF NOT EXISTS</c>), aangeleverde
    /// data-kolommen, de vaste audit-kolommen (mta_inserted/mta_modified/mta_deleted) en de
    /// synthetische, nooit-NULL business-key-kolom met de bijbehorende unieke index. Voegt
    /// daarnaast, indien <see cref="EntityDefinition.HasClubCode"/>, een secundaire index op
    /// ClubCode toe.
    /// <para>
    /// Geen aparte IDENTITY-surrogate-sleutel: geverifieerd tegen de daadwerkelijke SQL-Server-
    /// his-tabellen (<c>Database/his/Tables/Teams.sql</c> e.a.) en <c>sp_CreateTargetTableFromSource</c>
    /// dat de synthetische bk_-kolom (via een unieke index, geen PK-constraint) vandaag al de
    /// enige sleutel is — geen los surrogate-Id-kolom. <c>mta_deleted</c> is een nullable
    /// TIMESTAMP (deletie-tijdstip), geen boolean-vlag — idem geverifieerd tegen de bestaande
    /// his-tabellen, niet aangenomen.
    /// </para>
    /// </summary>
    public static string GenerateHisTable(EntityDefinition entity)
    {
        var table = PostgresIdentifier.Quote(entity.EntityName);
        var bkColumn = PostgresIdentifier.Quote(BusinessKeyColumnName(entity));
        var sb = new StringBuilder();

        sb.AppendLine($"CREATE TABLE IF NOT EXISTS his.{table} (");
        AppendColumnLines(sb, entity.Columns, forceTrailingComma: true);
        sb.AppendLine($"    {PostgresIdentifier.Quote("mta_inserted")} TIMESTAMP NOT NULL,");
        sb.AppendLine($"    {PostgresIdentifier.Quote("mta_modified")} TIMESTAMP NOT NULL,");
        sb.AppendLine($"    {PostgresIdentifier.Quote("mta_deleted")} TIMESTAMP NULL,");
        sb.AppendLine($"    {bkColumn} TEXT GENERATED ALWAYS AS ({BuildBusinessKeyExpression(entity)}) STORED");
        sb.AppendLine(");");
        sb.AppendLine(
            $"CREATE UNIQUE INDEX IF NOT EXISTS {PostgresIdentifier.Quote($"UQ_{entity.EntityName}_bk")} " +
            $"ON his.{table} ({bkColumn});");

        if (entity.HasClubCode)
        {
            sb.AppendLine(
                $"CREATE INDEX IF NOT EXISTS {PostgresIdentifier.Quote($"IX_{entity.EntityName}_ClubCode")} " +
                $"ON his.{table} ({PostgresIdentifier.Quote("ClubCode")});");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Bouwt de <c>COALESCE(...) || sep || COALESCE(...)</c>-expressie voor de synthetische
    /// business-key-kolom. Elke business-key-kolom mag NULL zijn (#818-addendum) — COALESCE naar
    /// een lege string voorkomt dat Postgres' NULL-als-distinct-gedrag in <c>UNIQUE</c>/
    /// <c>ON CONFLICT</c> een tweede rij toevoegt in plaats van de bestaande bij te werken,
    /// analoog aan SQL Server's bestaande ISNULL-gebaseerde bk_-kolom.
    /// </summary>
    internal static string BuildBusinessKeyExpression(EntityDefinition entity)
    {
        var parts = entity.BusinessKey.Select(col =>
            $"COALESCE({PostgresIdentifier.Quote(col)}::text, '')");
        return string.Join($" || '{BusinessKeySeparator}' || ", parts);
    }

    private static void AppendColumnLines(
        StringBuilder sb, IReadOnlyList<ColumnDefinition> columns, bool forceTrailingComma)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var isLast = i == columns.Count - 1;
            var nullability = column.IsNullable ? "" : " NOT NULL";
            var comma = (!isLast || forceTrailingComma) ? "," : "";
            sb.AppendLine(
                $"    {PostgresIdentifier.Quote(column.Name)} {PostgresTypeMapper.ToSqlType(column)}{nullability}{comma}");
        }
    }
}
