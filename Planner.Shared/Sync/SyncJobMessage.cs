namespace Planner.Shared.Sync;

/// <summary>Provider-neutral payload for a queued sync job.</summary>
public sealed class SyncJobMessage
{
    public Guid JobId { get; set; }
    public string ClubCode { get; set; } = "";
    public int WeekOffsetFrom { get; set; }
    public int WeekOffsetTo { get; set; }
}
