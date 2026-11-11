namespace Planner.Shared.Sync;

/// <summary>
/// Runs a queue job only after its durable store grants the pending-to-running claim.
/// The claim implementation remains tier-specific; this small orchestration is shared.
/// </summary>
public static class SyncJobClaimCore
{
    public static Task<bool> ExecuteIfClaimedAsync(
        Guid jobId,
        Func<Guid, Task<bool>> claimAsync,
        Func<Task> processClaimedAsync)
    {
        ArgumentNullException.ThrowIfNull(claimAsync);
        return ExecuteIfClaimedAsync(() => claimAsync(jobId), processClaimedAsync);
    }

    public static async Task<bool> ExecuteIfClaimedAsync(
        Func<Task<bool>> claimAsync,
        Func<Task> processClaimedAsync)
    {
        ArgumentNullException.ThrowIfNull(claimAsync);
        ArgumentNullException.ThrowIfNull(processClaimedAsync);

        if (!await claimAsync())
            return false;

        await processClaimedAsync();
        return true;
    }
}
