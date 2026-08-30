using Npgsql;

namespace Database.Postgres;

/// <summary>
/// Postgres-equivalent van <c>FunctionApp/MergeStgToHis.cs</c> (#818): voert de door
/// <see cref="PostgresSchemaGenerator"/>/<see cref="PostgresUpsertGenerator"/> gegenereerde SQL
/// uit tegen een Postgres-database via Npgsql. Alle dynamische logica (kolomtypen, business key,
/// change-detection) zit al vast in de gegenereerde stringen — deze klasse introspecteert zelf
/// niets, exact zoals de bestaande SQL-Server-orchestrator dat ook niet doet.
/// </summary>
public sealed class PostgresMergeOrchestrator
{
    private readonly string _connectionString;

    public PostgresMergeOrchestrator(string connectionString) => _connectionString = connectionString;

    /// <summary>Verwijdert en herbouwt de stg-tabel — analoog aan CreateTable.cs' bestaande
    /// DROP-TABLE-IF-EXISTS-patroon voor SQL Server.</summary>
    public async Task RecreateStgTableAsync(EntityDefinition entity, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, "stg", ct);
        await using var command = new NpgsqlCommand(PostgresSchemaGenerator.GenerateStgTable(entity), connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Zorgt dat de his-tabel bestaat (idempotent) — analoog aan
    /// sp_CreateTargetTableFromSource, maar hier vooraf C#-gegenereerd i.p.v.
    /// runtime-catalogus-introspectie.</summary>
    public async Task EnsureHisTableAsync(EntityDefinition entity, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, "his", ct);
        await using var command = new NpgsqlCommand(PostgresSchemaGenerator.GenerateHisTable(entity), connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Schept het <c>stg</c>/<c>his</c>-schema zelf als het nog niet bestaat. Vóór deze aanvulling
    /// bestond er geen enkele plek — productiecode of test — die deze schema's daadwerkelijk
    /// aanmaakte; elke test moest dat zelf via een losse <c>CREATE SCHEMA</c>-aanroep doen vóórdat
    /// de orchestrator iets kon uitvoeren. Idempotent, dus geen effect op een reeds bestaand schema.
    /// </summary>
    private static async Task EnsureSchemaAsync(NpgsqlConnection connection, string schema, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS {PostgresIdentifier.Quote(schema)};", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Voert de upsert van stg naar his uit voor deze entiteit — analoog aan
    /// sp_MergeStgToHis' MERGE-statement.</summary>
    public async Task MergeStgToHisAsync(EntityDefinition entity, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(PostgresUpsertGenerator.GenerateUpsertFromStgToHis(entity), connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
