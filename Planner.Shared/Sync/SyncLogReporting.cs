namespace Planner.Shared.Sync;

/// <summary>Best-effort facade for pipeline progress writes.</summary>
public static class SyncLogReporting
{
    public static string StatusForPartialFailure(bool failed) => failed ? "partial" : "succeeded";
    public static string? ReasonForFailure(bool failed, string reasonCode) => failed ? reasonCode : null;
    public static string? ProgrammaReason(bool truncated, bool failed) => truncated ? "programme_truncated" : failed ? "partial_failure" : null;
    public static string ReconciliationStatus(bool allSkipped, bool someSkipped, bool failed) =>
        allSkipped ? "skipped" : someSkipped || failed ? "partial" : "succeeded";
    public static string? ReconciliationReason(bool skipped, bool failed) =>
        skipped ? "reconciliation_skipped" : failed ? "operation_failed" : null;

    public static async Task BeginPhaseAsync(SyncLogWriter? writer, string phase)
    {
        if (writer is null) return;
        try { await writer.BeginPhaseAsync(phase, DateTime.UtcNow); } catch { }
    }

    public static async Task CompletePhaseAsync(SyncLogWriter? writer, string phase, string status,
        int? fetched = null, int? added = null, int? updated = null, int? failed = null, string? reasonCode = null)
    {
        if (writer is null) return;
        try { await writer.CompletePhaseAsync(phase, DateTime.UtcNow, status, fetched, added, updated, failed, reasonCode); } catch { }
    }

    public static async Task ReportProgressAsync(SyncLogWriter? writer, string phase,
        int? fetched = null, int? added = null, int? updated = null, int? failed = null, int? progress = null, int? total = null)
    {
        if (writer is null) return;
        try { await writer.ReportProgressAsync(phase, fetched, added, updated, failed, progress, total); } catch { }
    }

    public static async Task FailOpenPhasesAsync(SyncLogWriter? writer)
    {
        if (writer is null) return;
        foreach (var phase in new[]
        {
            SyncLogPolicy.PhaseConfiguratie, SyncLogPolicy.PhaseTeams, SyncLogPolicy.PhaseProgramma,
            SyncLogPolicy.PhaseUitslagen, SyncLogPolicy.PhaseMatchdetails, SyncLogPolicy.PhaseReconciliatie,
            SyncLogPolicy.PhasePlannerView, SyncLogPolicy.PhaseCanonicalisatie
        })
            await CompletePhaseAsync(writer, phase, SyncLogPolicy.StatusFailed,
                reasonCode: SyncLogPolicy.ReasonOperationFailed);
    }
}
