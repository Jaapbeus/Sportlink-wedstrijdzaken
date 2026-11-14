using Planner.Endpoints.Admin;
using Planner.Shared.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SportlinkFunction.Admin;
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

            if (!EgressGuard.ExternalIntegrationsAllowed())
            {
                log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — sync overgeslagen (#857).");
                return;
            }

            try
            {
                await WaitForDatabaseAsync(log);
                await SyncJobsRepository.HerstelOnderbrokenAsync(log); // #1839: herstel onderbroken jobs; loopt mee, geen eigen timer
                await AppSettings.LoadSettingsAsync(log);

                var uitkomst = await RunConfiguredSyncAsync(async () =>
                {
                    int toWeekOffset = await SeasonHelper.GetSeasonEndWeekOffsetAsync(log);
                    log.LogInformation("Sync range: weekOffset -1 to {ToWeekOffset} (end of season)", toWeekOffset);
                    return (-1, toWeekOffset);
                }, log);
                // #1779 (pariteit met de Postgres-tier, #1081): een run met mislukte fases (ook een
                // ongeldige sportlinkApiUrl) mag niet als Success eindigen en meldt zich als
                // getypeerde fout met faseoverzicht.
                uitkomst.GooiAlsMislukt();
            }
            catch (SyncAlreadyRunningException)
            {
                log.LogInformation("Synchronization skipped because another run owns the shared staging area.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "FetchAndStoreApiData fout");
                await FoutRapportage.RapporteerAsync(ex, "FetchAndStoreApiData", log);
                // #1779: opnieuw gooien, zoals de Postgres-timer (#1081); er staat geen retry-policy op.
                throw;
            }
        }

        /// <summary>
        /// HTTP trigger om handmatig een sync te starten.
        /// Default (geen params): vorige week t/m einde seizoen.
        /// Reset mode: GET /api/sync-matches?reset=true&amp;season=2024
        ///   Downloads all matches from the start of the given season year through end of current season.
        /// <para>
        /// <b>Autorisatie (#1350):</b> Easy Auth + rol <c>admin</c> via <see cref="AdminEndpoint.ExecuteAsync"/>,
        /// zoals elk ander beheerendpoint. Tot #1350 was dit het enige endpoint achter een Azure
        /// Function-<em>master key</em> (<c>AuthorizationLevel.Admin</c>): één statisch geheim zonder
        /// identiteit of audittrail, dat bovendien de volledige Function App beheert. Er bestond geen
        /// geautomatiseerde aanroeper die van die sleutel afhing — de nachtelijke sync is een
        /// timer-trigger in hetzelfde proces, en de deploy-smoketest bewees juist dat een key
        /// <em>geen</em> toegang geeft. De clubcode uit de wrapper wordt bewust genegeerd: een sync
        /// geldt altijd de primaire club uit <c>dbo.AppSettings</c>, nooit de democlub uit de
        /// GUI-clubswitcher.
        /// </para>
        /// </summary>
        [Function("SyncMatchesHttp")]
        public static Task<IActionResult> SyncMatchesHttp(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sync-matches")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("SyncMatchesHttp");
            return AdminEndpoint.ExecuteAsync(req, log, "handmatige Sportlink-sync",
                async _ =>
                {
                    log.LogInformation("HTTP trigger SyncMatchesHttp executed at: {Now}", DateTime.UtcNow);

                    if (UitgaandVerkeerCore.Blokkeer(EgressGuard.ExternalIntegrationsAllowed) is { } geblokkeerd) return geblokkeerd;

                    // Eigen catch naast de wrapper: de automatische foutrapportage hoort bij dit
                    // endpoint en niet bij de generieke 500 van de wrapper.
                    try
                    {
                        bool   isReset    = string.Equals(req.Query["reset"], "true", StringComparison.OrdinalIgnoreCase);
                        string? seasonParam = req.Query["season"];

                        var venster = (From: -1, To: 0);
                        var uitkomst = await RunConfiguredSyncAsync(async () =>
                        {
                            int toWeekOffset = await SeasonHelper.GetSeasonEndWeekOffsetAsync(log);
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
                            venster = (fromWeekOffset, toWeekOffset);
                            return venster;
                        }, log);
                        // #1779: pariteit met PostgresSyncMatchesHttp (#1081): 207 i.p.v. 200 bij mislukte
                        // fases, ook bij een ongeldige sportlinkApiUrl (fase "configuratie").
                        return SyncMatchesAntwoordCore.Bepaal(uitkomst, venster.From, venster.To,
                            $"Sync completed. WeekOffset range: {venster.From} to {venster.To}.");
                    }
                    catch (SyncAlreadyRunningException)
                    {
                        return new ObjectResult(new { status = "already running", message = SyncAlreadyRunningException.SafeMessage })
                        { StatusCode = StatusCodes.Status409Conflict };
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "SyncMatchesHttp fout");
                        await FoutRapportage.RapporteerAsync(ex, "SyncMatchesHttp", log);
                        return new StatusCodeResult(500);
                    }
                });
        }

        /// <summary>
        /// Gemeenschappelijk startpunt van timer, <c>SyncMatchesHttp</c> en <c>SyncJobProcessor</c>
        /// (#1138), pariteit met <c>SyncFunction.RunConfiguredSyncAsync</c> op de Postgres-tier (#1779):
        /// een ongeldige of ontbrekende <c>sportlinkApiUrl</c> (#1811: canonieke validatie) is de
        /// mislukte fase "configuratie", geen stille return; de logregel noemt de URL nooit.
        /// Het venster wordt pas ná de staging-lease en de seizoensdoorrol gebouwd (#1818).
        /// Internal (#1676): elke aanroeper controleert EgressGuard vooraf. Verwacht geladen AppSettings.
        /// </summary>
        internal static async Task<SyncUitkomst> RunConfiguredSyncAsync(
            Func<Task<(int FromWeekOffset, int ToWeekOffset)>> buildWindow, ILogger log)
        {
            if (!SportlinkDataserviceBasisUrl.TryGetCanonical(
                    AppSettings.GetSetting("sportlinkApiUrl"), out var sportlinkApiUrl))
            {
                log.LogError("Sportlink data-service base URL is invalid.");
                return SyncUitkomst.Configuratie();
            }

            string sportlinkClientId = $"clientId={AppSettings.GetSetting("sportlinkClientId")}";
            return await SportlinkSyncPipeline.RunSyncWithSeasonWindowAsync(buildWindow, sportlinkApiUrl, sportlinkClientId, log);
        }

        /// <summary>Opgeslagen venster van een job (#1138): zelfde pad, vaste offsets.</summary>
        internal static Task<SyncUitkomst> RunConfiguredSyncAsync(int fromWeekOffset, int toWeekOffset, ILogger log)
            => RunConfiguredSyncAsync(() => Task.FromResult((fromWeekOffset, toWeekOffset)), log);
    }
}
