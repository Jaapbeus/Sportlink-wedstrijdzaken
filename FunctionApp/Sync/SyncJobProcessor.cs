using SportlinkFunction.Infrastructure;
using Planner.Shared.Sync;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction;

/// <summary>
/// Verwerkt berichten van de <see cref="SyncJobsQueue.QueueName"/>-queue (#1138) — de opvolger van
/// de fire-and-forget <c>Task.Run</c> in <c>AdminSyncFunction.Trigger</c>. Eén poging per bericht:
/// een mislukking wordt in <c>dbo.SyncJobs</c> vastgelegd als <c>failed</c> en niet opnieuw
/// geprobeerd door de queue zelf (geen rethrow) — de sync-pipeline heeft al eigen retry-logica
/// voor databaseverbindingen, en een tweede, automatische queue-retry zou een gedeeltelijk
/// geslaagde merge riskeren te herhalen. Een beheerder die het opnieuw wil proberen klikt gewoon
/// nogmaals op de sync-knop, wat een nieuwe job aanmaakt.
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
                job.JobId, SyncJobsRepository.TryClaimPendingAsync, () => ProcessClaimedSqlServerJobAsync(job, log)))
        {
            log.LogInformation("SyncJobProcessor: SQL Server-job {JobId} is niet meer pending en wordt overgeslagen", job.JobId);
        }
    }

    private static async Task ProcessClaimedSqlServerJobAsync(SyncJobMessage job, ILogger log)
    {
        log.LogInformation("SyncJobProcessor: SQL Server job {JobId} claimed, range {From} .. {To}", job.JobId, job.WeekOffsetFrom, job.WeekOffsetTo);
        try
        {
            if (await BlokkeerBuitenProductieAsync(
                    job, log, EgressGuard.ExternalIntegrationsAllowed, SyncJobsRepository.MarkCompletedAsync))
                return;

            await WaitForDatabaseAsync(log);
            await AppSettings.LoadSettingsAsync(log);

            // #1779: een ongeldige of ontbrekende sportlinkApiUrl wordt de mislukte fase "configuratie"
            // (pariteit met de Postgres-tier); de jobfoutmelding noemt die fase.
            var uitkomst = await FetchAndStoreApiData.RunConfiguredSyncAsync(job.WeekOffsetFrom, job.WeekOffsetTo, log);
            if (uitkomst.GedeeltelijkMislukt)
            {
                log.LogWarning("SyncJobProcessor: job {JobId} gedeeltelijk mislukt", job.JobId);
                await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: false,
                    errorMessage: uitkomst.JobFoutmelding());
                return;
            }

            await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: true, errorMessage: null);
            log.LogInformation("SyncJobProcessor: job {JobId} voltooid", job.JobId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SyncJobProcessor: job {JobId} mislukt", job.JobId);
            await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: false,
                errorMessage: FoutmeldingVoorStatus(ex));
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
