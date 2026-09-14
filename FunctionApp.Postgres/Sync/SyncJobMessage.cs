namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Berichtinhoud op de <see cref="SyncJobsQueue.QueueName"/>-queue (#1138). JSON-geserialiseerd
/// door <c>AdminSyncFunction.Trigger</c>, gedeserialiseerd door <see cref="SyncJobProcessor"/>.
/// </summary>
internal sealed class SyncJobMessage
{
    public Guid JobId { get; set; }
    public string ClubCode { get; set; } = "";
    public int WeekOffsetFrom { get; set; }
    public int WeekOffsetTo { get; set; }
}
