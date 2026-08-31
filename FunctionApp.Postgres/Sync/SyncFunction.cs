using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.Infrastructure;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Function1.cs</c> (#890).
/// <para>
/// <b>Seizoensgrenzen (<c>dbo.Season</c>) zijn niet geport</b> — er bestaat geen Postgres-migratie
/// voor een seizoenstabel (zie docs/ARCHITECTUUR-DATABASE-TIERS.md). De SQL Server-tier se eigen
/// <c>SeasonHelper.GetSeasonEndWeekOffsetAsync</c> valt bij elke fout al terug op een hardcoded
/// <c>30</c> (~30 weken vooruit) — deze tier gebruikt diezelfde gedocumenteerde fallbackwaarde
/// rechtstreeks in plaats van een niet-bestaande tabel te bevragen. De reset-modus
/// (<c>?reset=true&amp;season=</c>), die <c>GetSeasonStartWeekOffsetAsync</c> nodig heeft, is
/// daarom bewust NIET vertaald — een expliciete 501 in plaats van een geraden seizoensstart.
/// </para>
/// </summary>
public static class SyncFunction
{
    // Zelfde gedocumenteerde fallback als SeasonHelper.GetSeasonEndWeekOffsetAsync op de SQL
    // Server-tier gebruikt zolang dbo.Season niet bereikbaar is — zie klasse-doc-comment.
    // Internal: ook hergebruikt door AdminSyncFunction.Trigger (#890).
    internal const int DefaultToWeekOffset = 30;

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
            await RunConfiguredSyncAsync(fromWeekOffset: -1, toWeekOffset: DefaultToWeekOffset, log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "PostgresFetchAndStoreApiData fout");
        }
    }

    [Function("PostgresSyncMatchesHttp")]
    public static async Task<IActionResult> SyncMatchesHttp(
        [HttpTrigger(AuthorizationLevel.Admin, "get", Route = "postgres/sync-matches")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("PostgresSyncMatchesHttp");
        log.LogInformation("HTTP trigger PostgresSyncMatchesHttp uitgevoerd om: {Now}", DateTime.UtcNow);

        if (string.Equals(req.Query["reset"], "true", StringComparison.OrdinalIgnoreCase))
        {
            return new ObjectResult(new
            {
                error = "Reset-modus (volledig seizoen opnieuw ophalen) vereist een seizoenstabel " +
                         "die nog niet bestaat op de Postgres-tier — zie issue 890. Standaardmodus " +
                         "(geen querystring) werkt al wel."
            })
            { StatusCode = 501 };
        }

        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            await RunConfiguredSyncAsync(fromWeekOffset: -1, toWeekOffset: DefaultToWeekOffset, log);
            return new OkObjectResult($"Sync voltooid. WeekOffset-bereik: -1 tot {DefaultToWeekOffset}.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "PostgresSyncMatchesHttp fout");
            return new StatusCodeResult(500);
        }
    }

    internal static async Task RunConfiguredSyncAsync(int fromWeekOffset, int toWeekOffset, ILogger log)
    {
        await PostgresAppSettings.LoadSettingsAsync(log);
        var clubCode = PostgresAppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubcode' ontbreekt in public.appsettings — sync kan niet doorgaan.");

        var connectionString = PostgresDatabaseConfig.ConnectionString;
        var (sportlinkApiUrl, sportlinkClientId) = await GetSportlinkConfigAsync(connectionString, clubCode);
        if (string.IsNullOrEmpty(sportlinkApiUrl))
        {
            log.LogError("sportlinkapiurl is niet geconfigureerd.");
            return;
        }

        await PostgresSyncPipeline.RunSyncAsync(
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
