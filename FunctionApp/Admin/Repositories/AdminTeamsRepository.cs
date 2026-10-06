using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal static class AdminTeamsRepository
{
    internal static async Task<List<string>> GetTeamnamenAsync(string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            SELECT [Teamnaam]
            FROM [dbo].[Teams]
            WHERE [ClubCode] = @Cc AND [IsActief] = 1
            ORDER BY [Teamnaam]", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await r.ReadAsync())
            list.Add(r.GetString(0));
        return list;
    }

    internal static async Task<List<object>> GetKeuzelijstAsync(string clubCode, string cs)
    {
        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
        using var cmd = new SqlCommand(@"
            SELECT [TeamId], [Teamnaam], [LeeftijdsCategorie]
            FROM [dbo].[Teams]
            WHERE [ClubCode] = @Cc AND [IsActief] = 1
            ORDER BY [Teamnaam]", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await r.ReadAsync())
            list.Add(new { teamId = r.GetInt32(0), teamnaam = r.GetString(1), leeftijdsCategorie = r.IsDBNull(2) ? null : r.GetString(2) });
        return list;
    }
}
