using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.Infrastructure;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Function1.cs</c> (#890).
/// <para>
/// <b>Seizoensgrenzen (<c>dbo.Season</c>)</b> zijn vertaald naar <c>public.season</c> (migratie
/// 008, #890) — zie <see cref="PostgresSeasonHelper"/>. Zowel de standaardsync (einde seizoen) als
/// de reset-modus (<c>?reset=true&amp;season=</c>, seizoensstart) gebruiken nu de echte tabel in
/// plaats van een geraden of geweigerde waarde.
/// </para>
/// </summary>
public static class SyncFunction
{
    [Function("PostgresFetchAndStoreApiData")]
    public static async Task Run([TimerTrigger("%FETCH_SCHEDULE%")] TimerInfo myTimer, FunctionContext context)
    {
        var log = context.GetLogger("PostgresFetchAndStoreApiData");
        log.LogInformation("Postgres-tier sync uitgevoerd om: {Now}", DateTime.UtcNow);

        if (!EgressGuard.ExternalIntegrationsAllowed())
        {
            log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — sync overgeslagen (#857).");
            return;
        }

        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            // #861: rol public.season zo nodig door vóór het venster gelezen wordt.
            await PostgresSeasonHelper.EnsureSeasonsAsync(log);
            var toWeekOffset = await PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync(log);
            var gedeeltelijkMislukt = await RunConfiguredSyncAsync(fromWeekOffset: -1, toWeekOffset, log);

            // #1081: een run met mislukte deelstappen mag niet als Success eindigen. Het
            // invocatie-overzicht in de portal toonde acht dagen 'Success 8 · Error 0' terwijl er
            // niets werd bijgewerkt (#1077) — een meetinstrument dat de verkeerde waarde geeft is
            // erger dan geen meetinstrument. Alle werk is op dit punt al gedaan; de uitzondering
            // markeert alleen de invocatie. Er staat geen retry-policy op deze functie, dus dit
            // leidt niet tot een herhaalde run van vijf minuten.
            if (gedeeltelijkMislukt)
                throw new InvalidOperationException(
                    "Synchronisatie gedeeltelijk mislukt — zie de voorgaande logregels voor de " +
                    "betrokken fase(s). lastsynctimestamp is bewust niet bijgewerkt.");
        }
        catch (Exception ex)
        {
            // Loggen én opnieuw gooien: zonder de rethrow slikt deze catch de uitzondering op en
            // rapporteert de host de invocatie als Success (#1081).
            log.LogError(ex, "PostgresFetchAndStoreApiData fout");
            throw;
        }
    }

    /// <summary>
    /// Default (geen params): vorige week t/m einde seizoen.
    /// Reset mode: GET /api/postgres/sync-matches?reset=true&amp;season=2024 — downloadt alle
    /// wedstrijden vanaf de start van het opgegeven seizoensjaar t/m het einde van het huidige
    /// seizoen. Zelfde gedrag als een ontbrekende/onparseerbare <c>season</c>-param bij het
    /// SQL Server-origineel: valt dan stil terug op de standaardmodus (fromWeekOffset = -1).
    /// </summary>
    [Function("PostgresSyncMatchesHttp")]
    public static async Task<IActionResult> SyncMatchesHttp(
        [HttpTrigger(AuthorizationLevel.Admin, "get", Route = "postgres/sync-matches")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("PostgresSyncMatchesHttp");
        log.LogInformation("HTTP trigger PostgresSyncMatchesHttp uitgevoerd om: {Now}", DateTime.UtcNow);

        var isReset = string.Equals(req.Query["reset"], "true", StringComparison.OrdinalIgnoreCase);
        string? seasonParam = req.Query["season"];

        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            // #861: rol public.season zo nodig door vóór het venster gelezen wordt.
            await PostgresSeasonHelper.EnsureSeasonsAsync(log);
            var toWeekOffset = await PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync(log);
            var fromWeekOffset = -1;

            if (isReset && int.TryParse(seasonParam, out var seasonStartYear))
            {
                fromWeekOffset = await PostgresSeasonHelper.GetSeasonStartWeekOffsetAsync(seasonStartYear, log);
                log.LogInformation("Reset mode: season {Year}, weekOffset {From} to {To}",
                    seasonStartYear, fromWeekOffset, toWeekOffset);
            }
            else
            {
                log.LogInformation("Default mode: weekOffset {From} to {To}", fromWeekOffset, toWeekOffset);
            }

            var gedeeltelijkMislukt = await RunConfiguredSyncAsync(fromWeekOffset, toWeekOffset, log);
            if (gedeeltelijkMislukt)
            {
                // #1081: 200 OK zou hier "klaar" betekenen terwijl lastsynctimestamp bewust niet is
                // bijgewerkt. 207 maakt het verschil zichtbaar zonder de geslaagde deelstappen weg
                // te gooien.
                return new ObjectResult(new
                {
                    status = "gedeeltelijk mislukt",
                    weekOffsetFrom = fromWeekOffset,
                    weekOffsetTo = toWeekOffset,
                    melding = "Eén of meer deelstappen zijn mislukt; lastsynctimestamp is niet bijgewerkt. "
                              + "Zie het functielog voor de betrokken fase(s)."
                })
                { StatusCode = StatusCodes.Status207MultiStatus };
            }
            return new OkObjectResult($"Sync voltooid. WeekOffset-bereik: {fromWeekOffset} tot {toWeekOffset}.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "PostgresSyncMatchesHttp fout");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>Geeft terug of er deelstappen zijn mislukt (#1081).</summary>
    internal static async Task<bool> RunConfiguredSyncAsync(int fromWeekOffset, int toWeekOffset, ILogger log)
    {
        await PostgresAppSettings.LoadSettingsAsync(log);
        var clubCode = PostgresAppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubcode' ontbreekt in public.appsettings — sync kan niet doorgaan.");

        var connectionString = PostgresDatabaseConfig.ConnectionString;
        var (sportlinkApiUrl, sportlinkClientId) = await GetSportlinkConfigAsync(connectionString, clubCode);
        if (string.IsNullOrEmpty(sportlinkApiUrl))
        {
            log.LogError("sportlinkapiurl is niet geconfigureerd.");
            return true;
        }

        return await PostgresSyncPipeline.RunSyncAsync(
            fromWeekOffset, toWeekOffset,
            sportlinkApiUrl, $"clientId={sportlinkClientId}",
            clubCode, connectionString, log);
    }

    private static async Task<(string apiUrl, string clientId)> GetSportlinkConfigAsync(string connectionString, string clubCode)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT sportlinkapiurl, sportlinkclientid FROM public.appsettings WHERE clubcode = @clubcode", connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return (string.Empty, string.Empty);
        return (reader.GetString(0), reader.GetString(1));
    }
}
