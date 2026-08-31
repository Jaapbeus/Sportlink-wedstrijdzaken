using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Email/IEmailPersistenceRepository.cs</c>'s <c>SqlEmailPersistenceRepository</c>
/// (#889) — de e-mailverwerkings-audit-trail/dedup-laag tegen <c>planner.emailverwerking</c>.
/// <para>
/// Vertaling: <c>SCOPE_IDENTITY()</c> → <c>RETURNING id</c>,
/// <c>SqlException.Number == 2601/2627</c> (unique violation) →
/// <c>PostgresException.SqlState == PostgresErrorCodes.UniqueViolation</c>, <c>GETUTCDATE()</c> →
/// <c>NOW()</c>, <c>TOP 1</c> → <c>LIMIT 1</c>.
/// </para>
/// <para>
/// <c>InsertClassificatieCorrectieAsync</c>/<c>HaalLeermomentVoorbeeldenOpAsync</c> delegeren naar
/// <see cref="LearningMomentRepository"/>, zelfde structuur als het origineel.
/// </para>
/// </summary>
internal static class SqlEmailPersistenceRepository
{
    internal static bool IsUniekeSleutelFout(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.UniqueViolation;

    internal static async Task<HashSet<string>> GetExcludedEmailAddressesAsync(string connectionString, string clubCode)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT emailadres FROM public.uitgeslotenemailadressen WHERE actief = TRUE AND clubcode = @clubcode",
            connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        var adressen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            adressen.Add(reader.GetString(0));
        return adressen;
    }

    internal static async Task<EmailVerwerkingStand?> HaalVerwerkingStandOpAsync(string connectionString, string messageId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, status, pogingen,
                   CASE WHEN isbeantwoord = TRUE OR verstuurdnaar IS NOT NULL THEN TRUE ELSE FALSE END AS antwoordverstuurd,
                   CASE WHEN verzendpogingoputc IS NOT NULL
                             AND isbeantwoord = FALSE
                             AND verstuurdnaar IS NULL THEN TRUE ELSE FALSE END AS verzendpogingonbeslist
            FROM planner.emailverwerking
            WHERE messageid = @messageid", conn);
        cmd.Parameters.AddWithValue("messageid", messageId);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new EmailVerwerkingStand(
            VerwerkingId: r.GetInt32(0),
            Status: r.IsDBNull(1) ? "" : r.GetString(1),
            Pogingen: r.IsDBNull(2) ? 0 : r.GetInt32(2),
            AntwoordVerstuurd: r.GetBoolean(3),
            VerzendPogingOnbeslist: r.GetBoolean(4));
    }

    internal static async Task VerhoogPogingenAsync(string connectionString, int verwerkingId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.emailverwerking
            SET pogingen = pogingen + 1, mta_modified = NOW()
            WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task InsertTeambegeleidingDoorsturenAuditAsync(
        string connectionString, string teamNaam, string aanvragerEmail, string ontvangersRegel, string clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO planner.emailverwerking
                (messageid, afzender, onderwerp, ontvangstdatum, verzoektype, status,
                 isbeantwoord, verstuurdnaar, clubcode, pogingen)
            VALUES
                (@messageid, @afzender, @onderwerp, NOW(), 'TeambegeleidingDoorsturen', 'AntwoordVerstuurd',
                 TRUE, @verstuurdnaar, @clubcode, 1)", conn);
        cmd.Parameters.AddWithValue("messageid", $"teambegeleiding-doorsturen-{Guid.NewGuid()}");
        cmd.Parameters.AddWithValue("afzender", aanvragerEmail);
        cmd.Parameters.AddWithValue("onderwerp", $"[{teamNaam}] Vraag doorgestuurd");
        cmd.Parameters.AddWithValue("verstuurdnaar", ontvangersRegel);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> InsertEmailVerwerkingAsync(string connectionString, InkomendBericht email, string clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO planner.emailverwerking
                (messageid, conversationid, afzender, onderwerp, ontvangstdatum, emailbody, verzoektype, status, clubcode, pogingen)
            VALUES
                (@messageid, @conversationid, @afzender, @onderwerp, @ontvangstdatum, @emailbody, 'Onbekend', 'Ontvangen', @clubcode, 1)
            RETURNING id", conn);
        cmd.Parameters.AddWithValue("messageid", email.MessageId);
        cmd.Parameters.AddWithValue("conversationid", (object?)email.ConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("afzender", email.Afzender);
        cmd.Parameters.AddWithValue("onderwerp", email.Onderwerp);
        cmd.Parameters.AddWithValue("ontvangstdatum", email.OntvangstDatum);
        cmd.Parameters.AddWithValue("emailbody", (object?)email.Body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("clubcode", clubCode);

        try
        {
            return (int)(await cmd.ExecuteScalarAsync())!;
        }
        catch (PostgresException ex) when (IsUniekeSleutelFout(ex))
        {
            throw new DubbeleMessageIdException(email.MessageId, ex);
        }
    }

    internal static async Task UpdateStatusAsync(
        string connectionString, int verwerkingId, EmailStatus status, string? geextraheerdeData)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var setClauses = "status = @status, mta_modified = NOW()";
        if (geextraheerdeData != null)
            setClauses += ", geextraheerdedata = @data, verzoektype = @verzoektype";

        await using var cmd = new NpgsqlCommand(
            $"UPDATE planner.emailverwerking SET {setClauses} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        cmd.Parameters.AddWithValue("status", status.ToString());
        if (geextraheerdeData != null)
        {
            cmd.Parameters.AddWithValue("data", geextraheerdeData);
            string verzoekType;
            try
            {
                var c = JObject.Parse(geextraheerdeData);
                verzoekType = c["Type"]?.ToString() ?? "Onbekend";
            }
            catch { verzoekType = "Onbekend"; }
            cmd.Parameters.AddWithValue("verzoektype", verzoekType);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task UpdatePlannerResponseAsync(string connectionString, int verwerkingId, string plannerResponseJson)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.emailverwerking
            SET plannerresponse = @response, mta_modified = NOW()
            WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        cmd.Parameters.AddWithValue("response", plannerResponseJson);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task UpdateAntwoordVerstuurdAsync(
        string connectionString, int verwerkingId, string verstuurdNaar, string antwoordEmail)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.emailverwerking
            SET status = 'AntwoordVerstuurd', isbeantwoord = TRUE, verstuurdnaar = @naar,
                antwoordemail = @antwoord, mta_modified = NOW()
            WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        cmd.Parameters.AddWithValue("naar", verstuurdNaar);
        cmd.Parameters.AddWithValue("antwoord", antwoordEmail);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task MarkeerVerzendPogingAsync(string connectionString, int verwerkingId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.emailverwerking
            SET verzendpogingoputc = NOW(), mta_modified = NOW()
            WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task WisVerzendPogingAsync(string connectionString, int verwerkingId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.emailverwerking
            SET verzendpogingoputc = NULL, mta_modified = NOW()
            WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task UpdateVoorgesteldAntwoordAsync(string connectionString, int verwerkingId, string antwoordEmail)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.emailverwerking
            SET status = 'Review', antwoordemail = @antwoord, mta_modified = NOW()
            WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        cmd.Parameters.AddWithValue("antwoord", antwoordEmail);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task UpdateFoutAsync(string connectionString, int verwerkingId, string foutMelding)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.emailverwerking
            SET status = 'Fout', foutmelding = @fout, mta_modified = NOW()
            WHERE id = @id
              AND isbeantwoord = FALSE
              AND verstuurdnaar IS NULL", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        cmd.Parameters.AddWithValue("fout", foutMelding.Length > 1000 ? foutMelding[..1000] : foutMelding);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<(bool IsReply, int? OrigineleVerwerkingId, string? OrigineelType, string? OriginaleSamenvatting)>
        DetecteerReplyOpOnsAntwoordAsync(string connectionString, string conversationId, string clubCode, ILogger log)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT id, verzoektype, geextraheerdedata
                FROM planner.emailverwerking
                WHERE conversationid = @conversationid
                  AND (isbeantwoord = TRUE OR verstuurdnaar IS NOT NULL)
                  AND clubcode = @clubcode
                ORDER BY mta_inserted DESC
                LIMIT 1", conn);
            cmd.Parameters.AddWithValue("conversationid", conversationId);
            cmd.Parameters.AddWithValue("clubcode", clubCode);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return (false, null, null, null);

            var id = r.GetInt32(0);
            var verzoekType = r.IsDBNull(1) ? null : r.GetString(1);
            var data = r.IsDBNull(2) ? null : r.GetString(2);

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

    internal static async Task UpdateReplyStatusAsync(
        string connectionString, int verwerkingId, bool isReply, int replyOpVerwerkingId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE planner.emailverwerking
            SET isreplyoponsantwoord = @isreply, replyopverwerkingid = @replyopid, mta_modified = NOW()
            WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", verwerkingId);
        cmd.Parameters.AddWithValue("isreply", isReply);
        cmd.Parameters.AddWithValue("replyopid", replyOpVerwerkingId);
        await cmd.ExecuteNonQueryAsync();
    }
}
