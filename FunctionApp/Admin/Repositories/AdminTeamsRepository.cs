using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal static class AdminTeamsRepository
{
    internal static async Task<List<string>> GetTeamnamenAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT DISTINCT [teamnaam]
            FROM [his].[teams]
            WHERE [ClubCode] = @Cc
            ORDER BY [teamnaam]", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await r.ReadAsync())
            list.Add(r.GetString(0));
        return list;
    }
}
