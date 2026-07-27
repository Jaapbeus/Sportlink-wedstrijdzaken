using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SportlinkFunction.Processing;

namespace SportlinkFunction.Email;

/// <summary>
/// SQL data-access voor planner.EmailVerwerking.
/// Extracted uit EmailProcessorFunction (#465).
/// </summary>
internal static class EmailProcessingRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    /// <summary>
    /// Leest de stand van een bestaande verwerking, of <c>null</c> als het bericht nog niet bekend is.
    /// <para>
    /// Bewust <b>niet</b> op ClubCode gefilterd: <c>UQ_EmailVerwerking_MessageId</c> is een globale
    /// unique constraint, dus dezelfde MessageId kan per definitie maar bij één club horen. Wél
    /// filteren zou een bestaande rij van een andere club onzichtbaar maken, waarna de INSERT op die
    /// unique constraint klapt en het bericht eeuwig blijft falen.
    /// </para>
    /// </summary>
    internal static async Task<EmailVerwerkingStand?> HaalVerwerkingStandOpAsync(string messageId)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [Id], [Status], [Pogingen],
                   CASE WHEN [VerstuurdNaar] IS NULL THEN 0 ELSE 1 END AS AntwoordVerstuurd
            FROM [planner].[EmailVerwerking]
            WHERE [MessageId] = @MessageId", conn);
        cmd.Parameters.AddWithValue("@MessageId", messageId);

        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new EmailVerwerkingStand(
            VerwerkingId: r.GetInt32(0),
            Status: r.IsDBNull(1) ? "" : r.GetString(1),
            Pogingen: r.IsDBNull(2) ? 0 : r.GetInt32(2),
            AntwoordVerstuurd: r.GetInt32(3) == 1);
    }

    internal static async Task VerhoogPogingenAsync(int verwerkingId)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [Pogingen] = [Pogingen] + 1, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id", verwerkingId);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> InsertEmailVerwerkingAsync(InkomendBericht email)
    {
        var clubCode = SystemUtilities.AppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        // Pogingen start op 1: deze insert hoort bij de eerste verwerkingspoging.
        using var cmd = new SqlCommand(@"
            INSERT INTO [planner].[EmailVerwerking]
                ([MessageId], [ConversationId], [Afzender], [Onderwerp], [OntvangstDatum], [EmailBody], [VerzoekType], [Status], [ClubCode], [Pogingen])
            VALUES
                (@MessageId, @ConversationId, @Afzender, @Onderwerp, @OntvangstDatum, @EmailBody, 'Onbekend', 'Ontvangen', @ClubCode, 1);
            SELECT CAST(SCOPE_IDENTITY() AS INT);", conn);
        cmd.Parameters.AddWithValue("@MessageId",     email.MessageId);
        cmd.Parameters.AddWithValue("@ConversationId",(object?)email.ConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Afzender",      email.Afzender);
        cmd.Parameters.AddWithValue("@Onderwerp",     email.Onderwerp);
        cmd.Parameters.AddWithValue("@OntvangstDatum",email.OntvangstDatum);
        cmd.Parameters.AddWithValue("@EmailBody",     (object?)email.Body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ClubCode",      clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();

        var setClauses = "[Status] = @Status, [mta_modified] = GETUTCDATE()";
        if (geextraheerdeData != null)
            setClauses += ", [GeextraheerdeData] = @Data, [VerzoekType] = @VerzoekType";

        using var cmd = new SqlCommand(
            $"UPDATE [planner].[EmailVerwerking] SET {setClauses} WHERE [Id] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id", verwerkingId);
        cmd.Parameters.AddWithValue("@Status", status.ToString());
        if (geextraheerdeData != null)
        {
            cmd.Parameters.AddWithValue("@Data", geextraheerdeData);
            try
            {
                var c = JsonConvert.DeserializeObject<BerichtClassificatie>(geextraheerdeData);
                cmd.Parameters.AddWithValue("@VerzoekType", c?.Type.ToString() ?? "Onbekend");
            }
            catch { cmd.Parameters.AddWithValue("@VerzoekType", "Onbekend"); }
        }
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [PlannerResponse] = @Response, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id",       verwerkingId);
        cmd.Parameters.AddWithValue("@Response", plannerResponseJson);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [Status] = 'AntwoordVerstuurd', [VerstuurdNaar] = @Naar, [AntwoordEmail] = @Antwoord, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id",      verwerkingId);
        cmd.Parameters.AddWithValue("@Naar",    verstuurdNaar);
        cmd.Parameters.AddWithValue("@Antwoord",antwoordEmail);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Slaat een voorgesteld antwoord op zonder te versturen (review mode, #712). <c>VerstuurdNaar</c>
    /// blijft leeg — dat is de duplicaatgrens van de idempotentie-guard én het criterium waarop
    /// <see cref="DetecteerReplyOpOnsAntwoordAsync"/> een eerder verzonden antwoord herkent.
    /// </summary>
    internal static async Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [Status] = 'Review', [AntwoordEmail] = @Antwoord, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id",       verwerkingId);
        cmd.Parameters.AddWithValue("@Antwoord", antwoordEmail);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task UpdateFoutAsync(string messageId, string foutMelding)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [Status] = 'Fout', [FoutMelding] = @Fout, [mta_modified] = GETUTCDATE()
            WHERE [MessageId] = @MessageId", conn);
        cmd.Parameters.AddWithValue("@MessageId", messageId);
        cmd.Parameters.AddWithValue("@Fout", foutMelding.Length > 1000 ? foutMelding[..1000] : foutMelding);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string conversationId, string clubCode, ILogger log)
    {
        try
        {
            using var conn = new SqlConnection(Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT TOP 1 [Id], [VerzoekType], [GeextraheerdeData]
                FROM [planner].[EmailVerwerking]
                WHERE [ConversationId] = @ConversationId
                  AND [VerstuurdNaar] IS NOT NULL
                  AND [ClubCode] = @ClubCode
                ORDER BY [mta_inserted] DESC", conn);
            cmd.Parameters.AddWithValue("@ConversationId", conversationId);
            cmd.Parameters.AddWithValue("@ClubCode", clubCode);

            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return (false, null, null, null);

            var id = r.GetInt32(0);
            var verzoekType = r.IsDBNull(1) ? null : r.GetString(1);
            var data        = r.IsDBNull(2) ? null : r.GetString(2);

            string? samenvatting = null;
            if (!string.IsNullOrEmpty(data))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("Samenvatting", out var s))
                        samenvatting = s.GetString();
                }
                catch { /* optioneel */ }
            }
            return (true, id, verzoekType, samenvatting);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Reply-detectie kon niet worden uitgevoerd — doorgaan als nieuw bericht");
            return (false, null, null, null);
        }
    }

    internal static async Task UpdateReplyStatusAsync(int verwerkingId, bool isReply, int replyOpVerwerkingId)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [IsReplyOpOnsAntwoord] = @IsReply, [ReplyOpVerwerkingId] = @ReplyOpId, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id",       verwerkingId);
        cmd.Parameters.AddWithValue("@IsReply",  isReply);
        cmd.Parameters.AddWithValue("@ReplyOpId",replyOpVerwerkingId);
        await cmd.ExecuteNonQueryAsync();
    }
}
