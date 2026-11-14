namespace Planner.Shared.Sync;

/// <summary>Privacyveilige fase-uitkomst van één sync-jobpoging (#1912).</summary>
public sealed record SyncLogEntry(
    Guid JobId,
    int Attempt,
    string Phase,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string Status,
    int? Fetched,
    int? Added,
    int? Updated,
    int? Failed,
    int? Progress,
    int? Total,
    string? ReasonCode);
