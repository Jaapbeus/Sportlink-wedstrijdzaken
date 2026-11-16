using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Email;

namespace FunctionApp.Postgres.Email;

/// <summary>One database snapshot per club; read failures always disable processing.</summary>
internal static class EmailVerwerkingsInstellingenOpslag
{
    internal static async Task<EmailVerwerkingsInstellingenLeesResultaat> LeesAsync(
        string connectionString, string clubCode, ILogger log)
    {
        return await EmailVerwerkingsInstellingenLezer.ProbeerLezenAsync(async () =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return await LeesAsync(connection, clubCode);
        }, () => log.LogWarning("E-mailinstellingen niet leesbaar; verwerking uit, review aan"));
    }

    internal static async Task<EmailVerwerkingsInstellingenLeesResultaat> LeesAsync(
        NpgsqlConnection connection, string clubCode, NpgsqlTransaction? transaction = null,
        bool vergrendel = false)
    {
        await using var command = new NpgsqlCommand(@"
            SELECT emailverwerkingingeschakeld, emailreviewmodusactief, emailreviewontvanger,
                   emailinstellingengeimporteerd, emailinstellingenbeheerdergewijzigd
            FROM public.appsettings WHERE clubcode = @clubcode" + (vergrendel ? " FOR UPDATE" : ""),
            connection, transaction);
        command.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return EmailVerwerkingsInstellingenLezer.LeesVeilig(null, true);
        return EmailVerwerkingsInstellingenLezer.VanRij(reader);
    }
}
