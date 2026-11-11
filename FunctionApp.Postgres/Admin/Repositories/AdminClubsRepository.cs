using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Leest de clubcode, weergavenaam en synchronisatiestatus voor de beheer-clublijst uit
/// <c>public.appsettings</c>. De SQL Server-kolommen <c>ClubCode</c>/<c>ClubName</c>/<c>SyncEnabled</c>
/// corresponderen met <c>clubcode</c>/<c>clubname</c>/<c>syncenabled</c>; Postgres gebruikt hiervoor
/// lowercase, ongequote identifiers (zie <c>docs/ARCHITECTUUR-DATABASE-TIERS.md</c> §3).
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
