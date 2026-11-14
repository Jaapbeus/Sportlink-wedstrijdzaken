using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests;

public sealed class SyncLogWriterTests
{
    private static readonly DateTime Start = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("partial", "partial_failure", true)]
    [InlineData("failed", "operation_failed", true)]
    [InlineData("skipped", "reconciliation_skipped", true)]
    [InlineData("skipped", "not_applicable", false)]
    [InlineData("succeeded", null, false)]
    public async Task IncompleteOutcome_IsRetainedEvenWhenLogWritesFail(string status, string? reason, bool incomplete)
    {
        var writer = new SyncLogWriter(Guid.NewGuid(), 1, _ => throw new InvalidOperationException("unavailable"));
        var now = DateTime.UtcNow;
        await writer.BeginPhaseAsync("reconciliatie", now);
        await writer.CompletePhaseAsync("reconciliatie", now.AddSeconds(1), status, reasonCode: reason);
        Assert.Equal(incomplete, writer.HasIncompletePhases);
    }

    [Fact]
    public async Task PartialReportsAndCompletionPreservePreviousCounters()
    {
        var entries = new List<SyncLogEntry>();
        var writer = NewWriter(entries);

        await writer.BeginPhaseAsync(SyncLogPolicy.PhaseTeams, Start);
        await writer.ReportProgressAsync(SyncLogPolicy.PhaseTeams, fetched: 12, added: 3, progress: 4, total: 12);
        await writer.ReportProgressAsync(SyncLogPolicy.PhaseTeams, updated: 2, progress: 5);
        await writer.CompletePhaseAsync(SyncLogPolicy.PhaseTeams, Start.AddMinutes(1), SyncLogPolicy.StatusSucceeded);

        var completed = Assert.Single(entries, entry => entry.CompletedAt is not null);
        Assert.Equal((12, 3, 2, 5, 12), (completed.Fetched, completed.Added, completed.Updated, completed.Progress, completed.Total));
    }

    [Fact]
    public async Task LateRepeatedAndInvalidCallbacksCannotChangeTerminalResult()
    {
        var entries = new List<SyncLogEntry>();
        var writer = NewWriter(entries);
        await writer.ReportProgressAsync(SyncLogPolicy.PhaseTeams, fetched: 1);
        await writer.CompletePhaseAsync(SyncLogPolicy.PhaseTeams, Start, SyncLogPolicy.StatusFailed);
        await writer.BeginPhaseAsync(SyncLogPolicy.PhaseTeams, Start);
        await writer.CompletePhaseAsync(SyncLogPolicy.PhaseTeams, Start.AddSeconds(1), SyncLogPolicy.StatusSkipped,
            reasonCode: SyncLogPolicy.ReasonNotApplicable);
        var count = entries.Count;

        await writer.BeginPhaseAsync(SyncLogPolicy.PhaseTeams, Start);
        await writer.ReportProgressAsync(SyncLogPolicy.PhaseTeams, fetched: 20);
        await writer.CompletePhaseAsync(SyncLogPolicy.PhaseTeams, Start.AddMinutes(2), SyncLogPolicy.StatusFailed);
        await writer.CompletePhaseAsync(SyncLogPolicy.PhaseTeams, Start.AddMinutes(2), "unknown");
        await writer.BeginPhaseAsync("unknown", DateTime.SpecifyKind(Start, DateTimeKind.Local));

        Assert.Equal(count, entries.Count);
        Assert.Equal(SyncLogPolicy.StatusSkipped, entries[^1].Status);
        Assert.False(SyncLogPolicy.IsReasonCode("unknown"));
        Assert.Throws<ArgumentException>(() => SyncLogPolicy.Validate(entries[^1] with { ReasonCode = "unknown" }));
    }

    [Fact]
    public async Task ProgressWritesAreBoundedAndStorageFailureDoesNotEscape()
    {
        var entries = new List<SyncLogEntry>();
        var writer = NewWriter(entries);
        await writer.BeginPhaseAsync(SyncLogPolicy.PhaseProgramma, Start);
        for (var count = 1; count <= 25; count++)
            await writer.ReportProgressAsync(SyncLogPolicy.PhaseProgramma, progress: count, total: 25);
        await writer.CompletePhaseAsync(SyncLogPolicy.PhaseProgramma, Start.AddMinutes(1), SyncLogPolicy.StatusSucceeded);

        Assert.Equal(4, entries.Count); // begin, every tenth progress, completion
        Assert.Equal(25, entries[^1].Progress);

        var broken = new SyncLogWriter(Guid.NewGuid(), 1, _ => throw new InvalidOperationException("storage unavailable"));
        await broken.BeginPhaseAsync(SyncLogPolicy.PhaseTeams, Start);
        await broken.ReportProgressAsync(SyncLogPolicy.PhaseTeams, fetched: 1);
        await broken.CompletePhaseAsync(SyncLogPolicy.PhaseTeams, Start.AddSeconds(1), SyncLogPolicy.StatusSucceeded);
    }

    private static SyncLogWriter NewWriter(List<SyncLogEntry> entries) =>
        new(Guid.NewGuid(), 1, entry =>
        {
            entries.Add(entry);
            return Task.CompletedTask;
        }, new FixedTimeProvider(Start));

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
