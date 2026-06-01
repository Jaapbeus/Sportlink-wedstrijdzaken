using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal static class AdminClubsRepository
{
    internal static async Task<List<object>> GetClubsAsync(string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [ClubCode], [ClubName], [SyncEnabled]
            FROM [dbo].[AppSettings]
            ORDER BY [SyncEnabled] DESC, [ClubName]", conn);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await r.ReadAsync())
            list.Add(new
            {
                clubCode    = r.GetString(0),
                clubName    = r.GetString(1),
                syncEnabled = !r.IsDBNull(2) && r.GetBoolean(2)
            });
        return list;
    }
}
