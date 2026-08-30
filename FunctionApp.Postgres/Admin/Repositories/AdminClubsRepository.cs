using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/AdminClubsRepository.cs</c>
/// (#887). Vertaling: <c>[dbo].[AppSettings]</c> → <c>public.appsettings</c> (lowercase, ongequote
/// — §3), <c>ClubCode</c>/<c>ClubName</c>/<c>SyncEnabled</c> → <c>clubcode</c>/<c>clubname</c>/
/// <c>syncenabled</c>. <c>clubname</c> bestaat sinds <c>003_admin_tables.sql</c> — de eerdere,
/// tijdelijke terugval op <c>clubcode</c> als weergavenaam is daarmee vervallen.
/// </summary>
internal static class AdminClubsRepository
{
    internal static async Task<List<object>> GetClubsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT clubcode, clubname, syncenabled FROM public.appsettings ORDER BY syncenabled DESC, clubname",
            connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await reader.ReadAsync())
            list.Add(new
            {
                clubCode = reader.GetString(0),
                clubName = reader.GetString(1),
                syncEnabled = !reader.IsDBNull(2) && reader.GetBoolean(2)
            });
        return list;
    }
}
