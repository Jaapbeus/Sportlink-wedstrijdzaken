using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SportlinkFunction.Processing;

namespace SportlinkFunction.Email;

/// <summary>
/// SQL data-access voor planner.ClassificatieCorrectie (leermomenten).
/// Extracted uit EmailProcessorFunction (#465).
/// </summary>
internal static class LearningMomentRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    internal static async Task InsertClassificatieCorrectieAsync(
        int origineleVerwerkingId, int correctionVerwerkingId,
        string origineelType, string? afgeleidType,
        string? originaleSamenvatting, string? correctieSamenvatting,
        string clubCode)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [planner].[ClassificatieCorrectie]
                ([OrigineleVerwerkingId], [CorrectionVerwerkingId], [OrigineelVerzoekType],
                 [AfgeleidJuistType], [OrigineleSamenvatting], [CorrectieSamenvatting], [ClubCode])
            VALUES
                (@OrigineleId, @CorrectionId, @OrigineelType,
                 @AfgeleidType, @OriginaleSamenvatting, @CorrectieSamenvatting, @ClubCode)", conn);
        cmd.Parameters.AddWithValue("@OrigineleId",          origineleVerwerkingId);
        cmd.Parameters.AddWithValue("@CorrectionId",         correctionVerwerkingId);
        cmd.Parameters.AddWithValue("@OrigineelType",         origineelType);
        cmd.Parameters.AddWithValue("@AfgeleidType",          (object?)afgeleidType          ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OriginaleSamenvatting", (object?)Truncate(originaleSamenvatting, 500) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CorrectieSamenvatting", (object?)Truncate(correctieSamenvatting, 500) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ClubCode",              clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<List<ClassificatieCorrectieVoorbeeld>> HaalVoorbeeldenOpAsync(string clubCode, ILogger log)
    {
        try
        {
            using var conn = new SqlConnection(Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT TOP 20 [OrigineelVerzoekType], [AfgeleidJuistType],
                              [OrigineleSamenvatting], [CorrectieSamenvatting]
                FROM [planner].[ClassificatieCorrectie]
                WHERE [IsGevalideerd] = 1 AND [IsAfgewezen] = 0 AND [ClubCode] = @ClubCode
                ORDER BY [mta_modified] DESC", conn);
            cmd.Parameters.AddWithValue("@ClubCode", clubCode);

            var list = new List<ClassificatieCorrectieVoorbeeld>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                if (r.IsDBNull(1)) continue;
                list.Add(new ClassificatieCorrectieVoorbeeld(
                    OrigineelType:       r.GetString(0),
                    JuistType:           r.GetString(1),
                    OrigineleSamenvatting:  r.IsDBNull(2) ? "" : r.GetString(2),
                    CorrectieSamenvatting:  r.IsDBNull(3) ? "" : r.GetString(3)));
            }
            return list;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Leermomenten konden niet worden geladen — classificatie zonder few-shots");
            return new List<ClassificatieCorrectieVoorbeeld>();
        }
    }

    private static string? Truncate(string? value, int max) =>
        value == null ? null : (value.Length > max ? value[..max] : value);
}
