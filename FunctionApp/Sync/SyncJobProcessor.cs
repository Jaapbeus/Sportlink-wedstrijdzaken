using SportlinkFunction.Infrastructure;
using Planner.Shared.Sync;
using System.Text.Json;
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
    public static async Task Run(
        [QueueTrigger(SyncJobsQueue.QueueName, Connection = "AzureWebJobsStorage")] string message,
        FunctionContext context)
    {
        var log = context.GetLogger("SyncJobProcessor");
        SyncJobMessage? job;
        try
        {
            job = JsonSerializer.Deserialize<SyncJobMessage>(message);
        }
        catch (JsonException ex)
        {
            log.LogError(ex, "SyncJobProcessor: bericht kon niet worden gelezen, overgeslagen: {Message}", message);
            return;
        }

        if (job is null || job.JobId == Guid.Empty)
        {
            log.LogError("SyncJobProcessor: leeg of ongeldig bericht overgeslagen: {Message}", message);
            return;
        }

        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["SyncJobId"] = job.JobId });
        if (!await SyncJobClaimCore.ExecuteIfClaimedAsync(
                () => SyncJobsRepository.TryClaimAttemptAsync(SystemUtilities.DatabaseConfig.ConnectionString, job.JobId), attempt => ProcessClaimedSqlServerJobAsync(job, attempt, log)))
        {
            log.LogInformation("SyncJobProcessor: SQL Server-job {JobId} is niet meer pending en wordt overgeslagen", job.JobId);
        }
    }

    private static async Task ProcessClaimedSqlServerJobAsync(SyncJobMessage job, int attempt, ILogger log)
    {
        log.LogInformation("SyncJobProcessor: SQL Server job {JobId} claimed (poging {Attempt}), range {From} .. {To}", job.JobId, attempt, job.WeekOffsetFrom, job.WeekOffsetTo);
        var lifecycleHandedOff = false;
        try
        {
            if (await BlokkeerBuitenProductieAsync(
                    job, log, EgressGuard.ExternalIntegrationsAllowed, (id, ok, msg) => SyncJobsRepository.MarkCompletedAsync(id, ok, msg, attempt)))
                return;

            await WaitForDatabaseAsync(log);
            await AppSettings.LoadSettingsAsync(log);
            var clubCode = AppSettings.GetSetting("clubCode")
                ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings.");
            lifecycleHandedOff = true;

            // #1779: een ongeldige of ontbrekende sportlinkApiUrl wordt de mislukte fase "configuratie"
            // (pariteit met de Postgres-tier); de jobfoutmelding noemt die fase.
            var uitkomst = await SyncJobRunCoordinator.RunClaimedAndCompleteAsync(
                job.JobId, attempt,
                entry => SyncLogRepository.UpsertAsync(clubCode, entry),
                async () => { await SyncLogRepository.CleanupAllAsync(DateTime.UtcNow); },
                (id, outcome, claimedAttempt, incomplete) => SyncJobsRepository.MarkOutcomeAsync(id, outcome, claimedAttempt, incomplete),
                (id, succeeded, error, claimedAttempt) => SyncJobsRepository.MarkCompletedAsync(id, succeeded, error, claimedAttempt),
                writer => FetchAndStoreApiData.RunConfiguredSyncAsync(job.WeekOffsetFrom, job.WeekOffsetTo, log, writer),
                FoutmeldingVoorStatus);
            if (uitkomst.GedeeltelijkMislukt)
            {
                log.LogWarning("SyncJobProcessor: job {JobId} gedeeltelijk mislukt", job.JobId);
            }
            else
                log.LogInformation("SyncJobProcessor: job {JobId} voltooid", job.JobId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SyncJobProcessor: job {JobId} mislukt", job.JobId);
            if (!lifecycleHandedOff)
            {
                await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: false,
                    errorMessage: FoutmeldingVoorStatus(ex), attempt);
            }
        }
    }

    internal static string FoutmeldingVoorStatus(Exception exception) =>
        $"Sync mislukt ({exception.GetType().Name}); raadpleeg het functielog voor details.";

    /// <summary>
    /// #1676: een bericht dat buiten productie binnenkomt wordt niet uitgevoerd en niet opnieuw
    /// aangeboden; de job wordt als mislukt vastgelegd zodat de beheerder de reden ziet.
    /// <c>true</c> betekent: <see cref="Run"/> stopt hier, vóór de sync. De job is dan al door
    /// de pending-to-running-overgang geclaimd. De poort en opslag komen binnen, zodat de test dit
    /// zonder database kan bewijzen.
    /// </summary>
    internal static async Task<bool> BlokkeerBuitenProductieAsync(
        SyncJobMessage job, ILogger log, Func<bool> externeIntegratiesToegestaan,
        Func<Guid, bool, string?, Task> markeerAfgerond)
    {
        if (externeIntegratiesToegestaan()) return false;

        log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — job {JobId} overgeslagen in dbo.SyncJobs (#857).", job.JobId);
        await markeerAfgerond(job.JobId, false, "Uitgaande integraties zijn buiten productie uitgeschakeld.");
        return true;
    }
}
