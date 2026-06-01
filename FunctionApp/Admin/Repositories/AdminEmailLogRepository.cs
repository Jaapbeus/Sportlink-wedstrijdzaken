using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal static class AdminEmailLogRepository
{
    // AVG: retourneert alleen metadata, NOOIT [EmailBody] of [AntwoordEmail].
    // Afzender wordt gemaskeerd: alleen domein zichtbaar.
    internal static async Task<List<Dictionary<string, object?>>> GetAsync(
        string clubCode, DateTime? vanaf, DateTime? tot, string? statusFilter, int limit, string cs)
    {
        var sql = @"SELECT TOP (@Limit) [Id], [MessageId], [ConversationId], [Afzender], [Onderwerp],
                           [OntvangstDatum], [VerzoekType], [Status], [VerstuurdNaar],
                           [FoutMelding], [mta_inserted], [mta_modified]
                    FROM [planner].[EmailVerwerking]
                    WHERE [ClubCode] = @Cc";
        if (vanaf.HasValue) sql += " AND [OntvangstDatum] >= @Vanaf";
        if (tot.HasValue)   sql += " AND [OntvangstDatum] < @Tot";
        if (!string.IsNullOrWhiteSpace(statusFilter)) sql += " AND [Status] = @Status";
        sql += " ORDER BY [OntvangstDatum] DESC";

        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Limit", limit);
        cmd.Parameters.AddWithValue("@Cc",    clubCode);
        if (vanaf.HasValue)   cmd.Parameters.AddWithValue("@Vanaf",  vanaf.Value);
        if (tot.HasValue)     cmd.Parameters.AddWithValue("@Tot",    tot.Value);
        if (!string.IsNullOrWhiteSpace(statusFilter))
            cmd.Parameters.AddWithValue("@Status", statusFilter);

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
            // AVG: mask afzender — only domain, never full address
            if (row.TryGetValue("Afzender", out var afz) && afz is string email)
            {
                var at = email.IndexOf('@');
                row["Afzender"] = at > 0 ? "***" + email[at..] : "***";
            }
            list.Add(row);
        }
        return list;
    }
}
