using System.Text.Json;
using Microsoft.Extensions.Logging;
using Planner.Shared.Sync;

namespace Planner.Endpoints.Sync;

/// <summary>Provider-neutral queue parsing, claim and completion lifecycle.</summary>
public static class SyncQueuedJobCore
{
    public static async Task<bool> SkipOutsideProductionAsync(Guid jobId,
        Func<bool> externalIntegrationsAllowed, Func<Guid, bool, string?, Task> complete)
    {
        if (externalIntegrationsAllowed()) return false;
        await complete(jobId, false, "Uitgaande integraties zijn buiten productie uitgeschakeld.");
        return true;
    }

    public static async Task RunAsync(string message, ILogger log,
        Func<Guid, Task<int?>> claim, Func<SyncJobMessage, int, Task> processClaimed)
    {
        SyncJobMessage? job;
        try { job = JsonSerializer.Deserialize<SyncJobMessage>(message); }
        catch (JsonException)
        {
            log.LogError("SyncJobProcessor: ongeldig queuebericht overgeslagen");
            return;
        }
        if (job is null || job.JobId == Guid.Empty)
        {
            log.LogError("SyncJobProcessor: leeg of ongeldig queuebericht overgeslagen");
            return;
        }

        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["SyncJobId"] = job.JobId });
        if (!await SyncJobClaimCore.ExecuteIfClaimedAsync(
                () => claim(job.JobId), attempt => processClaimed(job, attempt)))
            log.LogInformation("SyncJobProcessor: job {JobId} is niet meer pending", job.JobId);
    }

    public static async Task RunClaimedAsync(
        SyncJobMessage job, int attempt, ILogger log,
        Func<Task<string>> loadClubCode,
        Func<string, SyncLogEntry, Task> upsertLog,
        Func<Task> cleanupLogs,
        Func<Guid, SyncUitkomst, int, bool, Task> completeOutcome,
        Func<Guid, bool, string?, int, Task> completeFailure,
        Func<SyncJobMessage, SyncLogWriter, Task<SyncUitkomst>> execute)
    {
        var lifecycleHandedOff = false;
        try
        {
            var clubCode = await loadClubCode();
            lifecycleHandedOff = true;
            var outcome = await SyncJobRunCoordinator.RunClaimedAndCompleteAsync(
                job.JobId, attempt, new SyncJobRunServices(
                    entry => upsertLog(clubCode, entry), cleanupLogs, completeOutcome,
                    completeFailure, writer => execute(job, writer), SafeFailureMessage));
            if (outcome.GedeeltelijkMislukt)
                log.LogWarning("SyncJobProcessor: job {JobId} gedeeltelijk mislukt", job.JobId);
            else
                log.LogInformation("SyncJobProcessor: job {JobId} voltooid", job.JobId);
        }
        catch (Exception ex)
        {
            log.LogError("SyncJobProcessor: job {JobId} mislukt ({Type})", job.JobId, ex.GetType().Name);
            if (!lifecycleHandedOff)
                await completeFailure(job.JobId, false, SafeFailureMessage(ex), attempt);
        }
    }

    public static string SafeFailureMessage(Exception exception) =>
        $"Sync mislukt ({exception.GetType().Name}); raadpleeg het functielog voor details.";
}
