using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal static class AdminLeermomentenRepository
{
    internal static async Task<(int count, int limit, List<Dictionary<string, object?>> items)> GetAsync(
        string clubCode, string statusFilter, int limit, string cs)
    {
        var whereExtra = statusFilter switch
        {
            "pending"   => "AND [IsGevalideerd] = 0 AND [IsAfgewezen] = 0",
            "validated" => "AND [IsGevalideerd] = 1",
            "rejected"  => "AND [IsAfgewezen] = 1",
            _           => ""
        };
        var sql = $@"SELECT TOP (@Limit)
                    cc.[Id], cc.[OrigineleVerwerkingId], cc.[CorrectionVerwerkingId],
                    cc.[OrigineelVerzoekType], cc.[AfgeleidJuistType],
                    cc.[OrigineleSamenvatting], cc.[CorrectieSamenvatting],
                    cc.[IsGevalideerd], cc.[IsAfgewezen],
                    cc.[mta_inserted], cc.[mta_modified]
                FROM [planner].[ClassificatieCorrectie] cc
                WHERE cc.[ClubCode] = @Cc {whereExtra}
                ORDER BY cc.[mta_inserted] DESC";

        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Limit", limit);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < r.FieldCount; i++)
            {
                var raw = r.IsDBNull(i) ? null : r.GetValue(i);
                row[r.GetName(i)] = raw is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : raw;
            }
            list.Add(row);
        }
        return (list.Count, limit, list);
    }

    internal static async Task<(int pending, int validated, int rejected)> GetStatsAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT
                SUM(CASE WHEN [IsGevalideerd] = 0 AND [IsAfgewezen] = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN [IsGevalideerd] = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN [IsAfgewezen] = 1  THEN 1 ELSE 0 END)
            FROM [planner].[ClassificatieCorrectie]
            WHERE [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (0, 0, 0);
        return (r.IsDBNull(0) ? 0 : r.GetInt32(0),
                r.IsDBNull(1) ? 0 : r.GetInt32(1),
                r.IsDBNull(2) ? 0 : r.GetInt32(2));
    }

    internal static async Task<int> ValideerAsync(int id, bool isGevalideerd, bool isAfgewezen, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[ClassificatieCorrectie]
            SET [IsGevalideerd] = @IsGv, [IsAfgewezen] = @IsAf, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id",  id);
        cmd.Parameters.AddWithValue("@IsGv", isGevalideerd);
        cmd.Parameters.AddWithValue("@IsAf", isAfgewezen);
        cmd.Parameters.AddWithValue("@Cc",   clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
