namespace Planner.Shared.Sync;

/// <summary>Callbacks supplied by the active database tier for one claimed sync run.</summary>
public sealed record SyncJobRunServices(
    Func<SyncLogEntry, Task> Upsert,
    Func<Task> Cleanup,
    Func<Guid, SyncUitkomst, int, bool, Task> CompleteOutcome,
    Func<Guid, bool, string?, int, Task> CompleteFailure,
    Func<SyncLogWriter, Task<SyncUitkomst>> Execute,
    Func<Exception, string> SafeErrorMessage);

/// <summary>Provider-neutral job registration, claim, phase-writer and completion lifecycle.</summary>
public static class SyncJobRunCoordinator
{
    public static async Task<SyncUitkomst> RegisterClaimAndRunAsync(
        Func<Task<SyncJobCreation>> register,
        Func<Guid, Task<int?>> claim,
        SyncJobRunServices services)
    {
        var creation = await register();
        if (!creation.Created)
            throw new SyncAlreadyRunningException();

        var attempt = await claim(creation.Job.Id);
        if (attempt is null)
            throw new SyncAlreadyRunningException();

        return await RunClaimedAndCompleteAsync(creation.Job.Id, attempt.Value, services);
    }

    public static async Task<SyncUitkomst> RunClaimedAndCompleteAsync(
        Guid jobId, int attempt, SyncJobRunServices services)
    {
        var completed = false;
        SyncLogWriter? writer = null;
        try
        {
            writer = new SyncLogWriter(jobId, attempt, services.Upsert);
            try { await services.Cleanup(); } catch { }
            var result = await services.Execute(writer);
            await services.CompleteOutcome(jobId, result, attempt, writer.HasIncompletePhases);
            completed = true;
            return result;
        }
        catch (Exception ex)
        {
            await SyncLogReporting.FailOpenPhasesAsync(writer);
            if (!completed)
                await services.CompleteFailure(jobId, false, services.SafeErrorMessage(ex), attempt);
            throw;
        }
    }
}
