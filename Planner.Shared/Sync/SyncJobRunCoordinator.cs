namespace Planner.Shared.Sync;

/// <summary>Provider-neutral job registration, claim, phase-writer and completion lifecycle.</summary>
public static class SyncJobRunCoordinator
{
    public static async Task<SyncUitkomst> RegisterClaimAndRunAsync(
        Func<Task<SyncJobCreation>> register,
        Func<Guid, Task<int?>> claim,
        Func<SyncLogEntry, Task> upsert,
        Func<Task> cleanup,
        Func<Guid, SyncUitkomst, int, bool, Task> completeOutcome,
        Func<Guid, bool, string?, int, Task> completeFailure,
        Func<SyncLogWriter, Task<SyncUitkomst>> execute,
        Func<Exception, string> safeErrorMessage)
    {
        var creation = await register();
        if (!creation.Created)
            throw new SyncAlreadyRunningException();

        var attempt = await claim(creation.Job.Id);
        if (attempt is null)
            throw new SyncAlreadyRunningException();

        return await RunClaimedAndCompleteAsync(creation.Job.Id, attempt.Value, upsert, cleanup,
            completeOutcome, completeFailure, execute, safeErrorMessage);
    }

    public static async Task<SyncUitkomst> RunClaimedAndCompleteAsync(
        Guid jobId, int attempt,
        Func<SyncLogEntry, Task> upsert,
        Func<Task> cleanup,
        Func<Guid, SyncUitkomst, int, bool, Task> completeOutcome,
        Func<Guid, bool, string?, int, Task> completeFailure,
        Func<SyncLogWriter, Task<SyncUitkomst>> execute,
        Func<Exception, string> safeErrorMessage)
    {
        var completed = false;
        SyncLogWriter? writer = null;
        try
        {
            writer = new SyncLogWriter(jobId, attempt, upsert);
            try { await cleanup(); } catch { }
            var result = await execute(writer);
            await completeOutcome(jobId, result, attempt, writer.HasIncompletePhases);
            completed = true;
            return result;
        }
        catch (Exception ex)
        {
            await SyncLogReporting.FailOpenPhasesAsync(writer);
            if (!completed)
                await completeFailure(jobId, false, safeErrorMessage(ex), attempt);
            throw;
        }
    }
}
