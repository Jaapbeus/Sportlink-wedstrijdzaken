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

    /// <summary>
    /// Variant waarbij de claim het pogingnummer teruggeeft (#1839): <c>null</c> = niet geclaimd.
    /// Het nummer gaat naar de verwerking, die er alle afrondingen aan bindt zodat een verouderde
    /// poging een nieuwere niet kan overschrijven.
    /// </summary>
    public static async Task<bool> ExecuteIfClaimedAsync(
        Func<Task<int?>> claimAttemptAsync,
        Func<int, Task> processClaimedAsync)
    {
        ArgumentNullException.ThrowIfNull(claimAttemptAsync);
        ArgumentNullException.ThrowIfNull(processClaimedAsync);

        if (await claimAttemptAsync() is not { } attempt)
            return false;

        await processClaimedAsync(attempt);
        return true;
    }
}
