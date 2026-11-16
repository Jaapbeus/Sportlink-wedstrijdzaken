using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared.Email;
using SportlinkFunction.Email;

namespace SportlinkFunction.Admin;

/// <summary>Clubgebonden e-mailinstellingen binnen de bestaande admintransactie.</summary>
internal static class EmailSettingsAdminSupport
{
    internal static async Task VoegAanGetToeAsync(
        Dictionary<string, object?> result, string clubCode, ILogger log)
    {
        var gelezen = await EmailVerwerkingsInstellingenOpslag.LeesAsync(
            SystemUtilities.DatabaseConfig.ConnectionString, clubCode, log);
        EmailInstellingenAdminCore.VulGetResult(result, gelezen);
    }

    internal static async Task<bool> EmailKolommenBeschikbaarAsync(SqlConnection connection)
    {
        using var command = new SqlCommand(@"
            SELECT CASE WHEN
                COL_LENGTH('dbo.AppSettings', 'EmailVerwerkingIngeschakeld') IS NOT NULL AND
                COL_LENGTH('dbo.AppSettings', 'EmailReviewModusActief') IS NOT NULL AND
                COL_LENGTH('dbo.AppSettings', 'EmailReviewOntvanger') IS NOT NULL AND
                COL_LENGTH('dbo.AppSettings', 'EmailInstellingenGeimporteerd') IS NOT NULL AND
                COL_LENGTH('dbo.AppSettings', 'EmailInstellingenBeheerderGewijzigd') IS NOT NULL
            THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END", connection);
        return await command.ExecuteScalarAsync() is true;
    }

    internal static async Task<EmailInstellingenAdminWijziging?> BereidVoorAsync(
        SqlConnection connection, SqlTransaction transaction,
        Dictionary<string, string?> changes, string clubCode, bool bevestigd)
    {
        return await EmailInstellingenAdminCore.BereidVoorAsync(changes, bevestigd,
            () => EmailVerwerkingsInstellingenOpslag.LeesAsync(connection, clubCode, transaction, vergrendel: true));
    }

    internal static async Task RondAfAsync(SqlConnection connection, SqlTransaction transaction,
        string clubCode, string actor, EmailInstellingenAdminWijziging? wijziging)
    {
        if (wijziging is null) return;
        using var marker = new SqlCommand(
            "UPDATE [dbo].[AppSettings] SET [EmailInstellingenBeheerderGewijzigd] = 1 WHERE [ClubCode] = @ClubCode",
            connection, transaction);
        marker.Parameters.AddWithValue("@ClubCode", clubCode);
        await marker.ExecuteNonQueryAsync();
        if (!EmailInstellingenAdminCore.BevestigingAuditen(wijziging)) return;

        using var bevestigd = new SqlCommand(@"
            INSERT INTO [dbo].[AppSettingsAudit]
                ([GewijzigdDoor], [Veld], [OudeWaarde], [NieuweWaarde], [ClubCode])
            VALUES (@actor, 'EmailReviewUitschakelingBevestigd', 'actief', 'bevestigd', @clubcode)",
            connection, transaction);
        bevestigd.Parameters.AddWithValue("@actor", actor);
        bevestigd.Parameters.AddWithValue("@clubcode", clubCode);
        await bevestigd.ExecuteNonQueryAsync();
    }
    internal static async Task<Dictionary<string, string?>> ReadCurrentValuesAsync(
        SqlConnection connection, SqlTransaction transaction, IEnumerable<string> velden, string clubCode)
    {
        var safe = velden.Where(v => AdminSettingsFunction.AllowedFields.Contains(v, StringComparer.OrdinalIgnoreCase))
                         .Select(v => $"[{v}]")
                         .ToList();
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (safe.Count == 0) return result;

        // UPDLOCK, HOLDLOCK: de rij van de club blijft tot de commit vergrendeld (Codex-review #1743 ronde 1).
        var sql = $"SELECT TOP 1 {string.Join(", ", safe)} FROM [dbo].[AppSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [ClubCode] = @ClubCode";
        using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@ClubCode", clubCode);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return EmailInstellingenAdminCore.LeesWaarden(reader);
        return result;
    }

}
