using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SportlinkFunction.Infrastructure;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction
{
    /// <summary>
    /// Azure Functions triggers voor Sportlink-sync.
    /// Orchestratie-logica en staging-SQL zijn verplaatst naar:
    ///   - SportlinkSyncPipeline (FetchAndStore + RunSyncAsync)
    ///   - SportlinkStagingRepository (SQL staging writes)
    /// (#466)
    /// </summary>
    public static class FetchAndStoreApiData
    {
        [Function("FetchAndStoreApiData")]
        public static async Task Run([TimerTrigger("%FETCH_SCHEDULE%")] TimerInfo myTimer, FunctionContext context)
        {
            var log = context.GetLogger("FetchAndStoreApiData");
            log.LogInformation("Azure Function executed at: {Now}", DateTime.UtcNow);

            try
            {
                await WaitForDatabaseAsync(log);
                await AppSettings.LoadSettingsAsync(log);

                string? sportlinkApiUrl = AppSettings.GetSetting("sportlinkApiUrl");
                if (string.IsNullOrEmpty(sportlinkApiUrl))
                {
                    log.LogError("sportlinkApiUrl is not configured.");
                    return;
                }

                var syncEnabledStr = AppSettings.GetSetting("syncEnabled");
                if (syncEnabledStr == "0")
                {
                    log.LogInformation("SyncEnabled = 0 — sync overgeslagen voor deze club.");
                    return;
                }

                string sportlinkClientId = $"clientId={AppSettings.GetSetting("sportlinkClientId")}";
                int toWeekOffset = await SeasonHelper.GetSeasonEndWeekOffsetAsync(log);
                log.LogInformation("Sync range: weekOffset -1 to {ToWeekOffset} (end of season)", toWeekOffset);
                await SportlinkSyncPipeline.RunSyncAsync(-1, toWeekOffset, sportlinkApiUrl, sportlinkClientId, log);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "FetchAndStoreApiData fout");
                await GitHubIssueReporter.ReportAsync(ex, "FetchAndStoreApiData", log);
            }
        }

        /// <summary>
        /// HTTP trigger om handmatig een sync te starten.
        /// Default (geen params): vorige week t/m einde seizoen.
        /// Reset mode: GET /api/sync-matches?reset=true&amp;season=2024
        ///   Downloads all matches from the start of the given season year through end of current season.
        /// </summary>
        [Function("SyncMatchesHttp")]
        public static async Task<IActionResult> SyncMatchesHttp(
            [HttpTrigger(AuthorizationLevel.Admin, "get", Route = "sync-matches")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("SyncMatchesHttp");
            log.LogInformation("HTTP trigger SyncMatchesHttp executed at: {Now}", DateTime.UtcNow);

            try
            {
                await WaitForDatabaseAsync(log);
                await AppSettings.LoadSettingsAsync(log);

                string? sportlinkApiUrl = AppSettings.GetSetting("sportlinkApiUrl");
                if (string.IsNullOrEmpty(sportlinkApiUrl))
                {
                    log.LogError("sportlinkApiUrl is not configured.");
                    return new StatusCodeResult(500);
                }
                string sportlinkClientId = $"clientId={AppSettings.GetSetting("sportlinkClientId")}";

                bool   isReset    = string.Equals(req.Query["reset"], "true", StringComparison.OrdinalIgnoreCase);
                string? seasonParam = req.Query["season"];

                int toWeekOffset   = await SeasonHelper.GetSeasonEndWeekOffsetAsync(log);
                int fromWeekOffset = -1;

                if (isReset && int.TryParse(seasonParam, out int seasonStartYear))
                {
                    fromWeekOffset = await SeasonHelper.GetSeasonStartWeekOffsetAsync(seasonStartYear, log);
                    log.LogInformation("Reset mode: season {Year}, weekOffset {From} to {To}",
                        seasonStartYear, fromWeekOffset, toWeekOffset);
                }
                else
                {
                    log.LogInformation("Default mode: weekOffset {From} to {To}", fromWeekOffset, toWeekOffset);
                }

                await SportlinkSyncPipeline.RunSyncAsync(fromWeekOffset, toWeekOffset, sportlinkApiUrl, sportlinkClientId, log);
                return new OkObjectResult($"Sync completed. WeekOffset range: {fromWeekOffset} to {toWeekOffset}.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "SyncMatchesHttp fout");
                await GitHubIssueReporter.ReportAsync(ex, "SyncMatchesHttp", log);
                return new StatusCodeResult(500);
            }
        }

        // Publieke entry-point voor AdminSyncFunction (fire-and-forget achtergrondtaak).
        public static async Task RunSyncAsync(
            int fromWeekOffset, int toWeekOffset,
            string sportlinkApiUrl, string sportlinkClientId,
            ILogger log)
            => await SportlinkSyncPipeline.RunSyncAsync(fromWeekOffset, toWeekOffset, sportlinkApiUrl, sportlinkClientId, log);
    }
}
