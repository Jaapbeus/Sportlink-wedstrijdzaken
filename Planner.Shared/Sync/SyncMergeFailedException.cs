namespace Planner.Shared.Sync;

/// <summary>A sync merge failed; the public message intentionally excludes database details.</summary>
public sealed class SyncMergeFailedException : Exception
{
    public const string SafeMessage = "A synchronization merge failed.";

    public SyncMergeFailedException() : base(SafeMessage)
    {
    }
}
