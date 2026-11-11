namespace Planner.Shared.Sync;

/// <summary>
/// Indicates that another invocation currently owns the database-wide sync staging lease.
/// </summary>
public sealed class SyncAlreadyRunningException : Exception
{
    public const string SafeMessage = "A synchronization is already running. Try again later.";

    public SyncAlreadyRunningException() : base(SafeMessage)
    {
    }
}
