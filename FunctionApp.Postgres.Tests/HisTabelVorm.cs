using Database.Postgres;
using Npgsql;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Zorgt dat de ETL-tabellen in <c>his.*</c> de <b>productievorm</b> hebben voordat een test ze
/// gebruikt — ook als een andere testsuite ze in een andere vorm heeft achtergelaten.
///
/// <para>
/// <b>Dit is geen defensieve overdaad; het is empirisch nodig (issue 890).</b>
/// <c>Database.Postgres.Tests/PostgresMergeOrchestratorIntegrationTests</c> doet in zijn setup
/// expliciet <c>DROP TABLE IF EXISTS his."teams"/"matches"/"matchdetails" CASCADE</c> en bouwt ze
/// daarna opnieuw op uit <c>TestEntities</c> — dat zijn synthetische entiteiten met dezelfde
/// <i>namen</i> als de productie-entiteiten (<c>teams</c>, <c>matches</c>) maar met een veel
/// kleinere kolomverzameling, en twee van de vier zonder <c>clubcode</c>.
/// </para>
/// <para>
/// In de CI-job <c>fresh-db-postgres</c> draait die suite vlak vóór deze; zonder deze
/// herstelstap faalde elke test hier op <c>42703: column "clubcode" does not exist</c>. Omdat
/// <see cref="PostgresMergeOrchestrator.EnsureHisTableAsync"/> een <c>CREATE TABLE IF NOT EXISTS</c>
/// is, herstelt die zo'n afwijkende vorm uit zichzelf niet.
/// </para>
/// <para>
/// De controle vergelijkt tegen <see cref="KnownEntities"/> — de productiedefinitie (#818) — en
/// niet tegen een handmatig lijstje kolomnamen, zodat een nieuwe kolom daar automatisch meetelt.
/// </para>
/// <para>
/// De structurele oplossing (een eigen database of eigen schema per testsuite) staat als issue #925
/// open; deze klasse is bewust alleen een vangnet voor <c>his.*</c>.
/// </para>
/// </summary>
internal static class HisTabelVorm
{
    internal static async Task ZorgVoorProductievormAsync(string connectionString, params EntityDefinition[] entiteiten)
    {
        var orchestrator = new PostgresMergeOrchestrator(connectionString);

        foreach (var entiteit in entiteiten)
        {
            if (!await HeeftProductievormAsync(connectionString, entiteit))
                await DropAsync(connectionString, entiteit.EntityName);

            await orchestrator.EnsureHisTableAsync(entiteit);
        }
    }

    private static async Task<bool> HeeftProductievormAsync(string connectionString, EntityDefinition entiteit)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using var bestaat = new NpgsqlCommand("SELECT to_regclass(@naam) IS NOT NULL", conn);
        bestaat.Parameters.AddWithValue("naam", $"his.{entiteit.EntityName}");
        if (await bestaat.ExecuteScalarAsync() is not true)
            return true; // Bestaat nog niet — EnsureHisTableAsync maakt hem straks in de juiste vorm aan.

        await using var kolommen = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns " +
            "WHERE table_schema = 'his' AND table_name = @tabel", conn);
        kolommen.Parameters.AddWithValue("tabel", entiteit.EntityName);

        var aanwezig = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await kolommen.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) aanwezig.Add(reader.GetString(0));
        }

        return entiteit.Columns.All(c => aanwezig.Contains(c.Name));
    }

    private static async Task DropAsync(string connectionString, string entityName)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Ongeparametriseerd kan hier niet anders (een identifier is geen parameter); de naam komt
        // uitsluitend uit KnownEntities, nooit uit invoer.
        await using var cmd = new NpgsqlCommand(
            $"DROP TABLE IF EXISTS his.{PostgresIdentifier.Quote(entityName)} CASCADE", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
