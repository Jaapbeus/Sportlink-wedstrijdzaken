using Planner.Endpoints.Sync;
using Planner.Shared.Sync;
using FunctionApp.Postgres.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Sync;

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
            id => SyncJobsRepository.TryClaimAttemptAsync(PostgresDatabaseConfig.ConnectionString, id),
            (job, attempt) => ProcessClaimedPostgresJobAsync(job, attempt, log));
    }

    private static async Task ProcessClaimedPostgresJobAsync(SyncJobMessage job, int attempt, ILogger log)
    {
        if (await OverslaanZonderUitgaandVerkeerAsync(job.JobId, log,
                EgressGuard.ExternalIntegrationsAllowed, (id, ok, msg) => SyncJobsRepository.MarkCompletedAsync(id, ok, msg, attempt)))
            return;

        await SyncQueuedJobCore.RunClaimedAsync(job, attempt, log,
            async () =>
            {
                await PostgresAppSettings.LoadSettingsAsync(log);
                return PostgresAppSettings.GetSetting("clubCode")
                    ?? throw new InvalidOperationException("Vereiste instelling 'clubcode' ontbreekt in public.appsettings.");
            },
            (clubCode, entry) => SyncLogRepository.UpsertAsync(clubCode, entry),
            () => SyncLogRepository.CleanupAllAsync(DateTime.UtcNow),
            (id, outcome, claimedAttempt, incomplete) => SyncJobsRepository.MarkOutcomeAsync(id, outcome, claimedAttempt, incomplete),
            (id, succeeded, error, claimedAttempt) => SyncJobsRepository.MarkCompletedAsync(id, succeeded, error, claimedAttempt),
            (queued, writer) => SyncFunction.RunConfiguredSyncAsync(queued.WeekOffsetFrom, queued.WeekOffsetTo, log, writer));
    }

    /// <summary>
    /// Geeft <c>true</c> als de job buiten productie niet mag draaien; de job staat dan als mislukt
    /// in syncjobs en <see cref="Run"/> keert terug vóór RunConfiguredSyncAsync. De job is dan al
    /// door de pending-to-running-overgang geclaimd. Poort en opslag als parameters, zodat een
    /// unit-test dit zonder database bewijst (#1676).
    /// </summary>
    internal static Task<bool> OverslaanZonderUitgaandVerkeerAsync(
        Guid jobId, ILogger log, Func<bool> uitgaandVerkeerToegestaan,
        Func<Guid, bool, string?, Task> vastleggen) =>
        SyncQueuedJobCore.SkipOutsideProductionAsync(jobId, uitgaandVerkeerToegestaan, vastleggen);
}
