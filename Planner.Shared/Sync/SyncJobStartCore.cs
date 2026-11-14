namespace Planner.Shared.Sync;

/// <summary>Choose an existing active job or insert one under the tier's transaction lock.</summary>
public static class SyncJobStartCore
{
    public static async Task<SyncJobCreation> GetOrCreateAsync(
        Func<Task<SyncJobStatus?>> readActive,
        Func<Task<SyncJobStatus>> insert,
        Func<Task> commit)
    {
        var active = await readActive();
        if (active is not null)
        {
            await commit();
            return new(active, false);
        }

        var job = await insert();
        await commit();
        return new(job, true);
    }
}
