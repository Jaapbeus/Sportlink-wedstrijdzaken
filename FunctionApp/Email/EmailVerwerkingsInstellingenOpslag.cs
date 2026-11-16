using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared.Email;

namespace SportlinkFunction.Email;

/// <summary>One database snapshot per club; read failures always disable processing.</summary>
internal static class EmailVerwerkingsInstellingenOpslag
{
    internal static async Task<EmailVerwerkingsInstellingenLeesResultaat> LeesAsync(
        string connectionString, string clubCode, ILogger log)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            return await LeesAsync(connection, clubCode);
        }
        catch (Exception)
        {
            log.LogWarning("E-mailinstellingen niet leesbaar; verwerking uit, review aan");
            return EmailVerwerkingsInstellingenLezer.LeesVeilig(null, false);
        }
    }

    internal static async Task<EmailVerwerkingsInstellingenLeesResultaat> LeesAsync(
        SqlConnection connection, string clubCode, SqlTransaction? transaction = null,
        bool vergrendel = false)
    {
        using var command = new SqlCommand(@"
            SELECT [EmailVerwerkingIngeschakeld], [EmailReviewModusActief], [EmailReviewOntvanger],
                   [EmailInstellingenGeimporteerd], [EmailInstellingenBeheerderGewijzigd]
            FROM [dbo].[AppSettings] " + (vergrendel ? "WITH (UPDLOCK, HOLDLOCK) " : "") +
            "WHERE [ClubCode] = @clubcode", connection, transaction);
        command.Parameters.AddWithValue("@clubcode", clubCode);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return EmailVerwerkingsInstellingenLezer.LeesVeilig(null, true);
        var opgeslagen = new EmailVerwerkingsInstellingen(
            reader.GetBoolean(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3), reader.GetBoolean(4));
        return EmailVerwerkingsInstellingenLezer.LeesVeilig(opgeslagen, true);
    }
}
