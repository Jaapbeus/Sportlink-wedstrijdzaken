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
        // HeeftTrace (#1568): alleen een vlag, de trace zelf haalt de GUI op per regel.
        var sql = @"SELECT TOP (@Limit) v.[Id], v.[MessageId], v.[ConversationId], v.[Afzender], v.[Onderwerp],
                           v.[OntvangstDatum], v.[VerzoekType], v.[Status], v.[VerstuurdNaar],
                           v.[mta_inserted], v.[mta_modified],
                           CAST(CASE WHEN EXISTS (SELECT 1 FROM [planner].[EmailTrace] t
                                                  WHERE t.[VerwerkingId] = v.[Id] AND t.[ClubCode] = v.[ClubCode])
                                     THEN 1 ELSE 0 END AS BIT) AS [HeeftTrace]
                    FROM [planner].[EmailVerwerking] v
                    WHERE v.[ClubCode] = @Cc";
        if (vanaf.HasValue) sql += " AND v.[OntvangstDatum] >= @Vanaf";
        if (tot.HasValue)   sql += " AND v.[OntvangstDatum] < @Tot";
        if (!string.IsNullOrWhiteSpace(statusFilter)) sql += " AND v.[Status] = @Status";
        sql += " ORDER BY v.[OntvangstDatum] DESC";

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
