using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Planner;
using Microsoft.Azure.Functions.Worker;
using Npgsql;
using VoorbereidingCore = global::Planner.Endpoints.Sportlink.VeldplannerVoorbereidingCore;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Legt de blokken van de Sportlink-veldplanner en hun bondswedstrijdnummers vooraf vast voor de komende 4 weken (#1917),
/// zodat de Planning ze al in <c>public.sportlinkveldplannerblok</c> vindt. Zonder deze timer betaalt de eerste bezoeker van een
/// nieuwe datum ±40 opvragen bij Sportlink (gemeten 15,3 s tegen 2,8 s daarna). De hele stroom staat in
/// <see cref="VoorbereidingCore"/>; deze klasse levert alleen de aansluiting, de opslag en de datumquery. De SQL Server-tegenhanger is
/// <c>FunctionApp/Sportlink/VeldplannerVoorbereidingTimerFunction.cs</c>. Frequentie en reden: <see cref="VoorbereidingCore.Schema"/>.
/// </summary>
public static class VeldplannerVoorbereidingTimerFunction
{
    [Function("SportlinkVeldplannerVoorbereiding")]
    public static Task Run([TimerTrigger(VoorbereidingCore.Schema)] TimerInfo timer, FunctionContext context)
    {
        var log = context.GetLogger(VoorbereidingCore.FunctieNaam);
        var verbinding = PostgresDatabaseConfig.ConnectionString;

        return VoorbereidingCore.VoerTimerUitAsync(
            SportlinkEndpointSupport.ClientVoorTimer(context, log, "veldplanner-voorbereiding"),
            () => PostgresClubScope.Primary,
            SportlinkEndpointSupport.RolWedstrijdzaken,
            PostgresAppSettings.GetSetting("accommodatie"),
            (club, van, tot) => ZoekDatumsMetWedstrijdenAsync(verbinding, club, van, tot),
            new PostgresVeldplannerBlokStore(verbinding),
            log);
    }

    /// <summary>
    /// De datums in <paramref name="van"/>..<paramref name="tot"/> waarop de club eigen wedstrijden op de eigen accommodatie heeft:
    /// dezelfde voorwaarden als de Planning zelf (niet verwijderd, niet afgelast, accommodatie van de club), zodat de timer precies
    /// de datums voorbereidt die iemand kan openen.
    /// </summary>
    internal static async Task<IReadOnlyList<DateOnly>> ZoekDatumsMetWedstrijdenAsync(
        string connectionString, string clubCode, DateOnly van, DateOnly tot)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT m.kaledatum::date AS datum
            FROM his.matches m
            WHERE m.clubcode = @clubcode
              AND m.mta_deleted IS NULL
              AND m.status <> 'Afgelast'
              AND m.kaledatum::date BETWEEN @van AND @tot
              AND m.accommodatie LIKE '%' || (SELECT accommodatie FROM public.appsettings WHERE clubcode = @clubcode LIMIT 1) || '%'
            ORDER BY datum
            """, connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        command.Parameters.AddWithValue("van", van);
        command.Parameters.AddWithValue("tot", tot);

        var datums = new List<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            datums.Add(reader.GetFieldValue<DateOnly>(0));
        return datums;
    }
}
