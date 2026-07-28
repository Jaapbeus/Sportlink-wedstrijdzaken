using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SportlinkFunction.Processing;

namespace SportlinkFunction.Email;

/// <summary>
/// De INSERT in <c>planner.EmailVerwerking</c> botste op <c>UQ_EmailVerwerking_MessageId</c>: een
/// andere invocatie heeft dit bericht al geregistreerd.
/// <para>
/// Een eigen type omdat de aanroeper dit géén verwerkingsfout mag noemen — de foutafhandeling zoekt
/// op MessageId en zou daarmee de rij van die andere verwerking overschrijven, ook als die het
/// antwoord al verstuurd heeft. (#707)
/// </para>
/// </summary>
internal sealed class DubbeleMessageIdException : Exception
{
    internal DubbeleMessageIdException(string messageId, Exception inner)
        : base("MessageId is al geregistreerd in planner.EmailVerwerking", inner)
        => MessageId = messageId;

    internal string MessageId { get; }
}

/// <summary>
/// SQL data-access voor planner.EmailVerwerking.
/// Extracted uit EmailProcessorFunction (#465).
/// </summary>
internal static class EmailProcessingRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    /// <summary>
    /// Herkent een schending van een unique constraint (2627) of unique index (2601). Beide betekenen
    /// hier: deze MessageId staat al in de tabel.
    /// </summary>
    internal static bool IsUniekeSleutelFout(int sqlErrorNumber) => sqlErrorNumber is 2601 or 2627;

    private static bool BevatUniekeSleutelSchending(SqlException ex)
        => IsUniekeSleutelFout(ex.Number)
           || ex.Errors.Cast<SqlError>().Any(e => IsUniekeSleutelFout(e.Number));

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
        // AntwoordVerstuurd komt primair uit IsBeantwoord: die kolom overleeft de AVG-anonimisering,
        // VerstuurdNaar niet (#718). VerstuurdNaar blijft als terugvalpad staan voor rijen die vóór de
        // migratie zijn beantwoord en toen niet meer te backfillen waren.
        //
        // VerzendPogingOnbeslist: er is een verzendintentie vastgelegd die niet is gewist én er is geen
        // antwoord vastgelegd — dus verstuurd of misschien verstuurd, uitkomst onbekend (#716).
        using var cmd = new SqlCommand(@"
            SELECT [Id], [Status], [Pogingen],
                   CASE WHEN [IsBeantwoord] = 1 OR [VerstuurdNaar] IS NOT NULL THEN 1 ELSE 0 END AS AntwoordVerstuurd,
                   CASE WHEN [VerzendPogingOpUtc] IS NOT NULL
                             AND [IsBeantwoord] = 0
                             AND [VerstuurdNaar] IS NULL THEN 1 ELSE 0 END AS VerzendPogingOnbeslist
            FROM [planner].[EmailVerwerking]
            WHERE [MessageId] = @MessageId", conn);
        cmd.Parameters.AddWithValue("@MessageId", messageId);

        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new EmailVerwerkingStand(
            VerwerkingId: r.GetInt32(0),
            Status: r.IsDBNull(1) ? "" : r.GetString(1),
            Pogingen: r.IsDBNull(2) ? 0 : r.GetInt32(2),
            AntwoordVerstuurd: r.GetInt32(3) == 1,
            VerzendPogingOnbeslist: r.GetInt32(4) == 1);
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

    /// <summary>
    /// Registreert een nieuw bericht. Gooit <see cref="DubbeleMessageIdException"/> als een
    /// gelijktijdige invocatie deze MessageId al heeft vastgelegd.
    /// </summary>
    internal static async Task<int> InsertEmailVerwerkingAsync(InkomendBericht email)
    {
        // RequireClubCode weigert ook een lege waarde: LoadSettingsAsync zet een lege kolomwaarde als
        // "" in de cache, en met ClubCode "" zou de uitsluitingslijst leeg blijken — fail-open op een
        // AVG-maatregel. (#707)
        var clubCode = SystemUtilities.AppSettings.RequireClubCode();

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

        try
        {
            return (int)(await cmd.ExecuteScalarAsync())!;
        }
        catch (SqlException ex) when (BevatUniekeSleutelSchending(ex))
        {
            throw new DubbeleMessageIdException(email.MessageId, ex);
        }
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

    /// <summary>
    /// Legt vast dat het antwoord verstuurd is. <c>IsBeantwoord</c> wordt hier gezet en niet afgeleid
    /// van <c>VerstuurdNaar</c>: dat adres is een persoonsgegeven en verdwijnt na 30 dagen, het feit
    /// dat er geantwoord is moet blijven staan (#718).
    /// </summary>
    internal static async Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [Status] = 'AntwoordVerstuurd', [IsBeantwoord] = 1, [VerstuurdNaar] = @Naar,
                [AntwoordEmail] = @Antwoord, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id",      verwerkingId);
        cmd.Parameters.AddWithValue("@Naar",    verstuurdNaar);
        cmd.Parameters.AddWithValue("@Antwoord",antwoordEmail);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Zet de verzendintentie vlak vóór de verzendpoging (#716). Overschrijft een bestaande waarde
    /// bewust: bij een hervatte verwerking hoort het tijdstip van de huidige poging.
    /// </summary>
    internal static async Task MarkeerVerzendPogingAsync(int verwerkingId)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [VerzendPogingOpUtc] = GETUTCDATE(), [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id", verwerkingId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Wist de verzendintentie omdat het versturen aantoonbaar is mislukt (#716). Zonder dit wissen zou
    /// een échte verzendfout — het scenario van #712, waar juist wél opnieuw geprobeerd moet worden —
    /// niet meer van een onbekende uitkomst te onderscheiden zijn en op Review belanden.
    /// </summary>
    internal static async Task WisVerzendPogingAsync(int verwerkingId)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [VerzendPogingOpUtc] = NULL, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id", conn);
        cmd.Parameters.AddWithValue("@Id", verwerkingId);
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

    /// <summary>
    /// Legt een verwerkingsfout vast op <b>Id</b> in plaats van op MessageId (#717), consistent met
    /// elke andere mutatie op deze tabel.
    /// <para>
    /// De <c>IsBeantwoord = 0</c>-guard is het inhoudelijke deel: overlappen twee invocaties op
    /// hetzelfde bericht — de singleton-lease van een timer-trigger kan verlopen — dan mag de
    /// foutafhandeling van de ene niet de rij van de andere op 'Fout' zetten terwijl die het antwoord
    /// juist wél heeft verstuurd. Dat maakt van een verstuurd antwoord een 'Fout'-regel in het
    /// email-log, en de status is voor de coördinator het enige spoor.
    /// </para>
    /// </summary>
    internal static async Task UpdateFoutAsync(int verwerkingId, string foutMelding)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [planner].[EmailVerwerking]
            SET [Status] = 'Fout', [FoutMelding] = @Fout, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id
              AND [IsBeantwoord] = 0
              AND [VerstuurdNaar] IS NULL", conn);
        cmd.Parameters.AddWithValue("@Id", verwerkingId);
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
            // IsBeantwoord i.p.v. alleen VerstuurdNaar (#718): dat adres wordt na 30 dagen
            // geanonimiseerd, waardoor een reply op dag 31 — normaal bij een verzoek dat weken vooruit
            // ligt — niet meer als reply werd herkend en er dus ook geen leermoment meer uit kwam.
            using var cmd = new SqlCommand(@"
                SELECT TOP 1 [Id], [VerzoekType], [GeextraheerdeData]
                FROM [planner].[EmailVerwerking]
                WHERE [ConversationId] = @ConversationId
                  AND ([IsBeantwoord] = 1 OR [VerstuurdNaar] IS NOT NULL)
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
