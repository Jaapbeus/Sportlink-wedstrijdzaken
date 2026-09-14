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
        await SyncJobsRepository.MarkRunningAsync(job.JobId);
        log.LogInformation("SyncJobProcessor: job {JobId} gestart, range {From} .. {To}", job.JobId, job.WeekOffsetFrom, job.WeekOffsetTo);

        try
        {
            await WaitForDatabaseAsync(log);
            await AppSettings.LoadSettingsAsync(log);

            var sportlinkApiUrl = AppSettings.GetSetting("sportlinkApiUrl");
            if (string.IsNullOrEmpty(sportlinkApiUrl))
            {
                await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: false,
                    errorMessage: "sportlinkApiUrl niet geconfigureerd");
                return;
            }
            var sportlinkClientId = $"clientId={AppSettings.GetSetting("sportlinkClientId")}";

            var gedeeltelijkMislukt = await FetchAndStoreApiData.RunSyncAsync(
                job.WeekOffsetFrom, job.WeekOffsetTo, sportlinkApiUrl, sportlinkClientId, log);
            if (gedeeltelijkMislukt)
            {
                log.LogWarning("SyncJobProcessor: job {JobId} gedeeltelijk mislukt", job.JobId);
                await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: false,
                    errorMessage: "Sync gedeeltelijk mislukt — zie het functielog voor de betrokken fase(s).");
                return;
            }

            await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: true, errorMessage: null);
            log.LogInformation("SyncJobProcessor: job {JobId} voltooid", job.JobId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SyncJobProcessor: job {JobId} mislukt", job.JobId);
            await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: false, errorMessage: ex.Message);
        }
    }
}
