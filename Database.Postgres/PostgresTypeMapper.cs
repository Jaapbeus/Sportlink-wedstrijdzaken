namespace Database.Postgres;

/// <summary>
/// Vertaalt <see cref="ProviderAgnosticType"/> naar Postgres-DDL-typesyntax (#818). Zie
/// docs/ARCHITECTUUR-DATABASE-TIERS.md voor de bredere typemapping-tabel (SQL Server → Postgres)
/// waar deze klasse de Postgres-kant van implementeert.
/// </summary>
public static class PostgresTypeMapper
{
    public static string ToSqlType(ColumnDefinition column) => column.Type switch
    {
        ProviderAgnosticType.Integer => "INTEGER",
        ProviderAgnosticType.BigInt => "BIGINT",
        ProviderAgnosticType.Text => "TEXT",
        ProviderAgnosticType.VarChar => $"VARCHAR({RequireLength(column)})",
        // BIT -> BOOLEAN: Postgres staat geen impliciete integer-naar-boolean-coercion toe.
        // Elke plek die vandaag `= 0`/`= 1` tegen deze kolom vergelijkt, moet in de Postgres-tier
        // expliciet `= false`/`= true` gebruiken (#818, Randgevallen/risico's).
        ProviderAgnosticType.Boolean => "BOOLEAN",
        // DATETIME/DATETIME2 -> TIMESTAMP (zonder tijdzone, matcht SQL Server's eigen gedrag).
        ProviderAgnosticType.Timestamp => "TIMESTAMP",
        ProviderAgnosticType.Date => "DATE",
        ProviderAgnosticType.Time => "TIME",
        ProviderAgnosticType.Decimal => $"NUMERIC({RequirePrecision(column)},{column.Scale ?? 0})",
        _ => throw new NotSupportedException($"Onbekend ProviderAgnosticType: {column.Type}")
    };

    private static int RequireLength(ColumnDefinition column) =>
        column.Length ?? throw new InvalidOperationException(
            $"Kolom '{column.Name}': ProviderAgnosticType.VarChar vereist een Length.");

    private static int RequirePrecision(ColumnDefinition column) =>
        column.Precision ?? throw new InvalidOperationException(
            $"Kolom '{column.Name}': ProviderAgnosticType.Decimal vereist een Precision.");
}
