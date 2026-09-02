using Microsoft.Data.SqlClient;
using Planner.Shared;

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
                           [mta_inserted], [mta_modified]
                    FROM [planner].[EmailVerwerking]
                    WHERE [ClubCode] = @Cc";
        if (vanaf.HasValue) sql += " AND [OntvangstDatum] >= @Vanaf";
        if (tot.HasValue)   sql += " AND [OntvangstDatum] < @Tot";
        if (!string.IsNullOrWhiteSpace(statusFilter)) sql += " AND [Status] = @Status";
        sql += " ORDER BY [OntvangstDatum] DESC";

        using var conn = await AdminRepositoryHelpers.OpenConnectionAsync(cs);
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
            var row = AdminRepositoryHelpers.LeesAlleKolommenMetUtcDatums(r);
            // AVG (#858): via het gedeelde AvgMaskering — hoofdletterongevoelig, en het gooit
            // als er niets te maskeren viel in plaats van stil door te gaan.
            AvgMaskering.MaskeerAfzender(row);
            list.Add(row);
        }
        return list;
    }
}
