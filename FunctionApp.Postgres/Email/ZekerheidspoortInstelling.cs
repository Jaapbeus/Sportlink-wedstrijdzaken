using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Email.Trace;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// #1568 deel D: leest de clubinstelling "zekerheidspoort" (<c>public.appsettings.zekerheidspoortactief</c>,
/// migratie 040) per club. Alleen de databasevraag staat per tier; de fail-safe (ontbreekt = aan) en de
/// betekenis staan in <see cref="ZekerheidsPoort"/>.
/// </summary>
internal static class ZekerheidspoortInstelling
{
    public static Task<bool> IsActiefAsync(string connectionString, string clubCode, ILogger log)
        => ZekerheidsPoort.LeesVeiligAsync(async () =>
        {
            await using var verbinding = new NpgsqlConnection(connectionString);
            await verbinding.OpenAsync();
            await using var opdracht = new NpgsqlCommand(
                "SELECT zekerheidspoortactief FROM public.appsettings WHERE clubcode = @clubcode LIMIT 1", verbinding);
            opdracht.Parameters.AddWithValue("clubcode", clubCode);
            return await opdracht.ExecuteScalarAsync();
        }, log);
}
