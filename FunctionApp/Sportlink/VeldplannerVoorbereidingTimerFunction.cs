using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SportlinkFunction.Planner;
using VoorbereidingCore = global::Planner.Endpoints.Sportlink.VeldplannerVoorbereidingCore;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Sportlink/VeldplannerVoorbereidingTimerFunction.cs</c> (#1917): legt de
/// blokken van de Sportlink-veldplanner en hun bondswedstrijdnummers vooraf vast voor de komende 4 weken, zodat de Planning ze al in
/// <c>dbo.SportlinkVeldplannerBlok</c> vindt. De hele stroom staat in <see cref="VoorbereidingCore"/>. Frequentie en reden:
/// <see cref="VoorbereidingCore.Schema"/>.
/// </summary>
public static class VeldplannerVoorbereidingTimerFunction
{
    [Function("SportlinkVeldplannerVoorbereiding")]
    public static async Task Run([TimerTrigger(VoorbereidingCore.Schema)] TimerInfo myTimer, FunctionContext ctx)
    {
        var logger = ctx.GetLogger(VoorbereidingCore.FunctieNaam);

        // Zie SportlinkPublicMatchIdWarmupTimerFunction: deze tier laadt de instellingencache via WaitForDatabaseAsync,
        // vóór de toggle gelezen wordt.
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(logger);
        }
        catch (Exception ex)
        {
            logger.LogError("Database niet bereikbaar ({ErrorType}) — veldplanner voorbereiden overgeslagen.", ex.GetType().Name);
            return;
        }

        var connectionString = SystemUtilities.DatabaseConfig.ConnectionString;
        await VoorbereidingCore.VoerTimerUitAsync(
            SportlinkEndpointSupport.ClientVoorTimer(ctx, logger, "veldplanner-voorbereiding"),
            () => ClubScope.Primary,
            SportlinkEndpointSupport.RolWedstrijdzaken,
            SystemUtilities.AppSettings.GetSetting("accommodatie"),
            (club, van, tot) => ZoekDatumsMetWedstrijdenAsync(connectionString, club, van, tot),
            new SqlServerVeldplannerBlokStore(connectionString),
            logger);
    }

    /// <summary>
    /// De datums in <paramref name="van"/>..<paramref name="tot"/> waarop de club eigen wedstrijden op de eigen accommodatie heeft:
    /// dezelfde voorwaarden als de Planning zelf (niet afgelast, accommodatie van de club).
    /// </summary>
    internal static async Task<IReadOnlyList<DateOnly>> ZoekDatumsMetWedstrijdenAsync(
        string verbinding, string club, DateOnly van, DateOnly tot)
    {
        using var conn = new SqlConnection(verbinding);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("""
            SELECT DISTINCT CAST(m.[kaledatum] AS DATE) AS Datum
            FROM [his].[matches] m
            WHERE m.[ClubCode] = @ClubCode
              AND m.[status] <> 'Afgelast'
              AND CAST(m.[kaledatum] AS DATE) BETWEEN @Van AND @Tot
              AND m.[accommodatie] LIKE '%' + (SELECT TOP 1 [Accommodatie] FROM [dbo].[AppSettings] WHERE [ClubCode] = @ClubCode) + '%'
            ORDER BY Datum
            """, conn);
        cmd.Parameters.AddWithValue("@ClubCode", club);
        cmd.Parameters.AddWithValue("@Van", van.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@Tot", tot.ToDateTime(TimeOnly.MinValue));

        var dagen = new List<DateOnly>();
        using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
            dagen.Add(DateOnly.FromDateTime(rdr.GetDateTime(0)));
        return dagen;
    }
}
