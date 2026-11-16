using FunctionApp.Postgres.Email;
using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Email;

namespace FunctionApp.Postgres.Admin;

/// <summary>Clubgebonden e-mailinstellingen binnen de bestaande admintransactie.</summary>
internal static class EmailSettingsAdminSupport
{
    internal static async Task VoegAanGetToeAsync(
        Dictionary<string, object?> result, string clubCode, ILogger log)
    {
        var gelezen = await EmailVerwerkingsInstellingenOpslag.LeesAsync(
            PostgresDatabaseConfig.ConnectionString, clubCode, log);
        EmailInstellingenAdminCore.VulGetResult(result, gelezen);
    }

    internal static async Task<bool> EmailKolommenBeschikbaarAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(@"
            SELECT count(*) = 5 FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'appsettings'
              AND column_name IN ('emailverwerkingingeschakeld', 'emailreviewmodusactief',
                  'emailreviewontvanger', 'emailinstellingengeimporteerd',
                  'emailinstellingenbeheerdergewijzigd')", connection);
        return await command.ExecuteScalarAsync() is true;
    }

    internal static async Task<EmailInstellingenAdminWijziging?> BereidVoorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Dictionary<string, string?> changes, string clubCode, bool bevestigd)
    {
        return await EmailInstellingenAdminCore.BereidVoorAsync(changes, bevestigd,
            () => EmailVerwerkingsInstellingenOpslag.LeesAsync(connection, clubCode, transaction, vergrendel: true));
    }

    internal static async Task RondAfAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string clubCode, string actor, EmailInstellingenAdminWijziging? wijziging)
    {
        if (wijziging is null) return;
        await using var marker = new NpgsqlCommand(
            "UPDATE public.appsettings SET emailinstellingenbeheerdergewijzigd = true WHERE clubcode = @clubcode",
            connection, transaction);
        marker.Parameters.AddWithValue("clubcode", clubCode);
        await marker.ExecuteNonQueryAsync();
        if (!EmailInstellingenAdminCore.BevestigingAuditen(wijziging)) return;

        await using var bevestigd = new NpgsqlCommand(@"
            INSERT INTO public.appsettingsaudit
                (gewijzigddoor, veld, oudewaarde, nieuwewaarde, clubcode)
            VALUES (@actor, 'EmailReviewUitschakelingBevestigd', 'actief', 'bevestigd', @clubcode)",
            connection, transaction);
        bevestigd.Parameters.AddWithValue("actor", actor);
        bevestigd.Parameters.AddWithValue("clubcode", clubCode);
        await bevestigd.ExecuteNonQueryAsync();
    }
    internal static async Task<Dictionary<string, string?>> ReadCurrentValuesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<string> velden, string clubCode)
    {
        var safe = velden.Where(v => AdminSettingsFunction.AllowedFields.Contains(v, StringComparer.OrdinalIgnoreCase))
                         .Select(v => $"{v} AS \"{v}\"")
                         .ToList();
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (safe.Count == 0) return result;

        var sql = $"SELECT {string.Join(", ", safe)} FROM public.appsettings WHERE clubcode = @clubcode LIMIT 1 FOR UPDATE";
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return EmailInstellingenAdminCore.LeesWaarden(reader);
        return result;
    }

}
