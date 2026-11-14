using Planner.Endpoints.Sync;
using SportlinkFunction.Infrastructure;
using Planner.Shared.Sync;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction;

/// <summary>
/// Verwerkt berichten van de <see cref="SyncJobsQueue.QueueName"/>-queue (#1138) — de opvolger van
/// de fire-and-forget <c>Task.Run</c> in <c>AdminSyncFunction.Trigger</c>. Een normale mislukking
/// (fout of gedeeltelijk mislukt) wordt vastgelegd als <c>failed</c> en niet door de queue herhaald
/// (geen rethrow): een tweede queue-retry zou een gedeeltelijk geslaagde merge riskeren te herhalen.
/// Alleen een onderbroken job (30 minuten op <c>running</c>, bv. een gecrashte host) wordt door
/// <see cref="SyncJobsRepository.HerstelOnderbrokenAsync"/> begrensd opnieuw aangeboden (#1839, hoogstens 2 keer; het herstel loopt mee met een sync-start, een statusaanvraag en de geplande sync); elke
/// afronding is aan het pogingnummer van de claim gebonden zodat een verouderde poging een
/// herstarte niet overschrijft.
/// </summary>
public static class SyncJobProcessor
{
    [Function("SyncJobProcessor")]
    public static Task Run(
        [QueueTrigger(SyncJobsQueue.QueueName, Connection = "AzureWebJobsStorage")] string message,
        FunctionContext context)
    {
        var log = context.GetLogger("SyncJobProcessor");
        return SyncQueuedJobCore.RunAsync(message, log,
            id => SyncJobsRepository.TryClaimAttemptAsync(SystemUtilities.DatabaseConfig.ConnectionString, id),
            (job, attempt) => ProcessClaimedSqlServerJobAsync(job, attempt, log));
    }

    private static async Task ProcessClaimedSqlServerJobAsync(SyncJobMessage job, int attempt, ILogger log)
    {
        if (await BlokkeerBuitenProductieAsync(job, log,
                EgressGuard.ExternalIntegrationsAllowed, (id, ok, msg) => SyncJobsRepository.MarkCompletedAsync(id, ok, msg, attempt)))
            return;

        await SyncQueuedJobCore.RunClaimedAsync(job, attempt, log,
            async () =>
            {
                await WaitForDatabaseAsync(log);
                await AppSettings.LoadSettingsAsync(log);
                return AppSettings.GetSetting("clubCode")
                    ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings.");
            },
            (clubCode, entry) => SyncLogRepository.UpsertAsync(clubCode, entry),
            () => SyncLogRepository.CleanupAllAsync(DateTime.UtcNow),
            (id, outcome, claimedAttempt, incomplete) => SyncJobsRepository.MarkOutcomeAsync(id, outcome, claimedAttempt, incomplete),
            (id, succeeded, error, claimedAttempt) => SyncJobsRepository.MarkCompletedAsync(id, succeeded, error, claimedAttempt),
            (queued, writer) => FetchAndStoreApiData.RunConfiguredSyncAsync(queued.WeekOffsetFrom, queued.WeekOffsetTo, log, writer));
    }

    /// <summary>
    /// #1676: een bericht dat buiten productie binnenkomt wordt niet uitgevoerd en niet opnieuw
    /// aangeboden; de job wordt als mislukt vastgelegd zodat de beheerder de reden ziet.
    /// <c>true</c> betekent: <see cref="Run"/> stopt hier, vóór de sync. De job is dan al door
    /// de pending-to-running-overgang geclaimd. De poort en opslag komen binnen, zodat de test dit
    /// zonder database kan bewijzen.
    /// </summary>
    internal static Task<bool> BlokkeerBuitenProductieAsync(
        SyncJobMessage job, ILogger log, Func<bool> externeIntegratiesToegestaan,
        Func<Guid, bool, string?, Task> markeerAfgerond) =>
        SyncQueuedJobCore.SkipOutsideProductionAsync(job.JobId, externeIntegratiesToegestaan, markeerAfgerond);
}
