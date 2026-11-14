using Planner.Endpoints.Admin;
using FunctionApp.Postgres.Infrastructure;
using System.Text.Json;
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
                () => SyncJobsRepository.TryClaimAttemptAsync(PostgresDatabaseConfig.ConnectionString, job.JobId), attempt => ProcessClaimedPostgresJobAsync(job, attempt, log)))
        {
            log.LogInformation("SyncJobProcessor: Postgres-job {JobId} is not pending and will be skipped", job.JobId);
        }
    }

    private static async Task ProcessClaimedPostgresJobAsync(SyncJobMessage job, int attempt, ILogger log)
    {
        log.LogInformation("SyncJobProcessor: Postgres job {JobId} claimed (poging {Attempt}), range {From} .. {To}", job.JobId, attempt, job.WeekOffsetFrom, job.WeekOffsetTo);
        try
        {
            // #1676: na het claimen wordt een bericht buiten productie niet uitgevoerd en
            // als mislukt vastgelegd; een duplicaat kan die status niet overschrijven.
            if (await OverslaanZonderUitgaandVerkeerAsync(job.JobId, log,
                    EgressGuard.ExternalIntegrationsAllowed, (id, ok, msg) => SyncJobsRepository.MarkCompletedAsync(id, ok, msg, attempt)))
                return;

            var uitkomst = await SyncFunction.RunConfiguredSyncAsync(job.WeekOffsetFrom, job.WeekOffsetTo, log);
            if (uitkomst.GedeeltelijkMislukt)
            {
                log.LogWarning("SyncJobProcessor: job {JobId} gedeeltelijk mislukt", job.JobId);
                await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: false,
                    errorMessage: uitkomst.JobFoutmelding(), attempt);
                return;
            }

            await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: true, errorMessage: null, attempt);
            log.LogInformation("SyncJobProcessor: job {JobId} voltooid", job.JobId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SyncJobProcessor: job {JobId} mislukt", job.JobId);
            await SyncJobsRepository.MarkCompletedAsync(job.JobId, succeeded: false,
                errorMessage: FoutmeldingVoorStatus(ex), attempt);
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
