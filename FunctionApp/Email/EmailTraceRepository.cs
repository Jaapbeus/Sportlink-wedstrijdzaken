using Microsoft.Data.SqlClient;
using Planner.Shared.Email.Trace;

namespace SportlinkFunction.Email;

/// <summary>
/// SQL data-access voor <c>planner.EmailTrace</c> (#1568, deel B); Postgres-tegenhanger:
/// <c>FunctionApp.Postgres/Email/EmailTraceRepository.cs</c>. Het record is PII-arm (zie
/// <see cref="EmailTraceRecord"/>). Geen foreign key naar <c>planner.EmailVerwerking</c>: de trace
/// overleeft de cleanup van de verwerking, dus de status komt uit een LEFT JOIN en is <c>null</c>
/// als de verwerking al is opgeruimd.
/// </summary>
internal static class EmailTraceRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    /// <summary>Idempotent: een retry van dezelfde verwerking vervangt de eerdere trace.</summary>
    internal static async Task UpsertAsync(EmailTraceRecord record)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            MERGE [planner].[EmailTrace] WITH (HOLDLOCK) AS doel
            USING (SELECT @VerwerkingId AS [VerwerkingId]) AS bron ON doel.[VerwerkingId] = bron.[VerwerkingId]
            WHEN MATCHED THEN UPDATE SET
                [ClubCode] = @ClubCode, [Aangemaakt] = GETUTCDATE(), [VerzoekType] = @VerzoekType,
                [Zekerheid] = @Zekerheid, [SjabloonSleutel] = @Sjabloon, [TraceJson] = @TraceJson,
                [AppVersie] = @AppVersie
            WHEN NOT MATCHED THEN INSERT
                ([VerwerkingId], [ClubCode], [VerzoekType], [Zekerheid], [SjabloonSleutel], [TraceJson], [AppVersie])
                VALUES (@VerwerkingId, @ClubCode, @VerzoekType, @Zekerheid, @Sjabloon, @TraceJson, @AppVersie);", conn);
        cmd.Parameters.AddWithValue("@VerwerkingId", record.VerwerkingId);
        cmd.Parameters.AddWithValue("@ClubCode", record.ClubCode);
        cmd.Parameters.AddWithValue("@VerzoekType", record.VerzoekType);
        cmd.Parameters.AddWithValue("@Zekerheid", record.Zekerheid);
        cmd.Parameters.AddWithValue("@Sjabloon", (object?)record.SjabloonSleutel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TraceJson", record.TraceJson);
        cmd.Parameters.AddWithValue("@AppVersie", record.AppVersie);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Leest de trace van één verwerking van deze club; <c>null</c> als er geen is.</summary>
    internal static async Task<EmailTraceAntwoord?> HaalOpAsync(string connectionString, string clubCode, int verwerkingId)
    {
        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT t.[VerzoekType], v.[Status], v.[OntvangstDatum], t.[Aangemaakt], t.[Zekerheid],
                   t.[SjabloonSleutel], t.[AppVersie], t.[TraceJson]
            FROM [planner].[EmailTrace] t
            LEFT JOIN [planner].[EmailVerwerking] v ON v.[Id] = t.[VerwerkingId] AND v.[ClubCode] = t.[ClubCode]
            WHERE t.[VerwerkingId] = @VerwerkingId AND t.[ClubCode] = @ClubCode", conn);
        cmd.Parameters.AddWithValue("@VerwerkingId", verwerkingId);
        cmd.Parameters.AddWithValue("@ClubCode", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return EmailTraceAntwoord.VanRij(verwerkingId, r);
    }
}
