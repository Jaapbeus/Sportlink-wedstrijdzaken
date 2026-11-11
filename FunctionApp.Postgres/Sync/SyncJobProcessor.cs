using Planner.Endpoints.Admin;
using FunctionApp.Postgres.Infrastructure;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Verwerkt berichten van de <see cref="SyncJobsQueue.QueueName"/>-queue (#1138) — de opvolger van
/// de fire-and-forget <c>Task.Run</c> in <c>AdminSyncFunction.Trigger</c>. Eén poging per bericht:
/// een mislukking wordt in <c>syncjobs</c> vastgelegd als <c>failed</c> en niet opnieuw geprobeerd
/// door de queue zelf (geen rethrow) — de sync-pipeline heeft al eigen retry-logica voor
/// databaseverbindingen, en een tweede, automatische queue-retry zou een gedeeltelijk geslaagde
/// merge riskeren te herhalen. Een beheerder die het opnieuw wil proberen klikt gewoon nogmaals op
/// de sync-knop, wat een nieuwe job aanmaakt.
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
        if (!await global::Planner.Shared.Sync.SyncJobClaimCore.ExecuteIfClaimedAsync(
                job.JobId, SyncJobsRepository.TryClaimPendingAsync, () => ProcessClaimedPostgresJobAsync(job, log)))
        {
            log.LogInformation("SyncJobProcessor: Postgres-job {JobId} is not pending and will be skipped", job.JobId);
        }
    }

    private static async Task ProcessClaimedPostgresJobAsync(SyncJobMessage job, ILogger log)
    {
        log.LogInformation("SyncJobProcessor: Postgres job {JobId} claimed, range {From} .. {To}", job.JobId, job.WeekOffsetFrom, job.WeekOffsetTo);
        try
        {
            // #1676: na het claimen wordt een bericht buiten productie niet uitgevoerd en
            // als mislukt vastgelegd; een duplicaat kan die status niet overschrijven.
            if (await OverslaanZonderUitgaandVerkeerAsync(job.JobId, log,
                    EgressGuard.ExternalIntegrationsAllowed, SyncJobsRepository.MarkCompletedAsync))
                return;

            var gedeeltelijkMislukt = await SyncFunction.RunConfiguredSyncAsync(job.WeekOffsetFrom, job.WeekOffsetTo, log);
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
            await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: false,
                errorMessage: FoutmeldingVoorStatus(ex));
        }
    }

    internal static string FoutmeldingVoorStatus(Exception exception) =>
        $"Sync mislukt ({exception.GetType().Name}); raadpleeg het functielog voor details.";

    /// <summary>
    /// Geeft <c>true</c> als de job buiten productie niet mag draaien; de job staat dan als mislukt
    /// in syncjobs en <see cref="Run"/> keert terug vóór RunConfiguredSyncAsync. De job is dan al
    /// door de pending-to-running-overgang geclaimd. Poort en opslag als parameters, zodat een
    /// unit-test dit zonder database bewijst (#1676).
    /// </summary>
    internal static async Task<bool> OverslaanZonderUitgaandVerkeerAsync(
        Guid jobId, ILogger log, Func<bool> uitgaandVerkeerToegestaan,
        Func<Guid, bool, string?, Task> vastleggen)
    {
        var geblokkeerd = UitgaandVerkeerCore.Blokkeer(uitgaandVerkeerToegestaan) is not null;
        if (geblokkeerd)
        {
            log.LogInformation("EgressGuard: uitgaande integraties geblokkeerd buiten productie — job {JobId} overgeslagen in syncjobs (#857).", jobId);
            await vastleggen(jobId, false, "Uitgaande integraties zijn buiten productie uitgeschakeld.");
        }
        return geblokkeerd;
    }
}
