using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/AdminClubsRepository.cs</c>
/// (#887). Vertaling: <c>[dbo].[AppSettings]</c> → <c>public.appsettings</c> (lowercase, ongequote
/// — §3), <c>ClubCode</c>/<c>ClubName</c>/<c>SyncEnabled</c> → <c>clubcode</c>/<c>accommodatie</c>-
/// achtige lowercase kolomnamen.
/// <para>
/// <b>Functioneel verschil met de SQL Server-tier, expliciet:</b> <c>public.appsettings</c> heeft
/// vandaag geen <c>clubname</c>-kolom (zie <c>Database.Postgres/migrations/001_baseline.sql</c>) —
/// alleen <c>clubcode</c>, <c>accommodatie</c>, <c>syncenabled</c>. Deze vertaling gebruikt
/// <c>clubcode</c> ook als weergavenaam totdat een toekomstige migratie een echte
/// <c>clubname</c>-kolom toevoegt; dat is geen aanname maar een bewust, hier gedocumenteerd gat
/// (te herzien zodra #862 of een vervolgmigratie dat kolomverschil dicht).
/// </para>
/// </summary>
internal static class AdminClubsRepository
{
    internal static async Task<List<object>> GetClubsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT clubcode, syncenabled FROM public.appsettings ORDER BY syncenabled DESC, clubcode",
            connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await reader.ReadAsync())
            list.Add(new
            {
                clubCode = reader.GetString(0),
                clubName = reader.GetString(0),
                syncEnabled = reader.GetBoolean(1)
            });
        return list;
    }
}
