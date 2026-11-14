using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests;

public sealed class SyncJobRunCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterClaimAndRunAsync_CompletesTypedOutcomeAndIgnoresLogFailures(bool partial)
    {
        var id = Guid.NewGuid();
        var outcome = new SyncUitkomst();
        if (partial) outcome.Registreer(SyncUitkomst.FaseProgramma, true);
        SyncUitkomst? completedOutcome = null;
        var completionCalls = 0;

        var result = await SyncJobRunCoordinator.RegisterClaimAndRunAsync(
            () => Task.FromResult(Creation(id, created: true)),
            _ => Task.FromResult<int?>(1),
            _ => throw new InvalidOperationException("log write failure"),
            () => throw new InvalidOperationException("cleanup failure"),
            (_, value, _, _) => { completedOutcome = value; return Task.CompletedTask; },
            (_, _, _, _) => { completionCalls++; return Task.CompletedTask; },
            async writer =>
            {
                await writer.BeginPhaseAsync(SyncLogPolicy.PhaseTeams, DateTime.UtcNow);
                return outcome;
            },
            _ => "safe failure");

        Assert.Same(outcome, result);
        Assert.Same(outcome, completedOutcome);
        Assert.Equal(0, completionCalls);
    }

    [Fact]
    public async Task RegisterClaimAndRunAsync_WhenExecutionThrows_ClosesOpenPhaseAndMarksFailure()
    {
        SyncLogEntry? terminalPhase = null;
        (Guid Id, bool Succeeded, string? Message, int Attempt)? failure = null;

        var act = () => SyncJobRunCoordinator.RegisterClaimAndRunAsync(
            () => Task.FromResult(Creation(Guid.NewGuid(), created: true)),
            _ => Task.FromResult<int?>(2),
            entry => { terminalPhase = entry.CompletedAt is null ? terminalPhase : entry; return Task.CompletedTask; },
            () => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (id, succeeded, message, attempt) => { failure = (id, succeeded, message, attempt); return Task.CompletedTask; },
            async writer =>
            {
                await writer.BeginPhaseAsync(SyncLogPolicy.PhaseTeams, DateTime.UtcNow);
                throw new InvalidOperationException("private exception text");
            },
            _ => "fixed safe failure");

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.NotNull(terminalPhase);
        Assert.Equal(SyncLogPolicy.StatusFailed, terminalPhase!.Status);
        Assert.Equal(SyncLogPolicy.ReasonOperationFailed, terminalPhase.ReasonCode);
        Assert.NotNull(failure);
        Assert.False(failure!.Value.Succeeded);
        Assert.Equal("fixed safe failure", failure.Value.Message);
        Assert.Equal(2, failure.Value.Attempt);
    }

    [Fact]
    public async Task RegisterClaimAndRunAsync_WhenBestEffortPhaseIsPartial_PersistsIncompleteJobOutcome()
    {
        var incomplete = false;
        var outcome = new SyncUitkomst();

        await SyncJobRunCoordinator.RegisterClaimAndRunAsync(
            () => Task.FromResult(Creation(Guid.NewGuid(), created: true)),
            _ => Task.FromResult<int?>(1),
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            (_, _, _, hasIncompletePhases) => { incomplete = hasIncompletePhases; return Task.CompletedTask; },
            (_, _, _, _) => Task.CompletedTask,
            async writer =>
            {
                await writer.BeginPhaseAsync(SyncLogPolicy.PhaseCanonicalisatie, DateTime.UtcNow);
                await writer.CompletePhaseAsync(SyncLogPolicy.PhaseCanonicalisatie, DateTime.UtcNow,
                    SyncLogPolicy.StatusPartial, failed: 1, reasonCode: SyncLogPolicy.ReasonPartialFailure);
                return outcome;
            },
            _ => "safe failure");

        Assert.True(incomplete);
    }

    [Fact]
    public async Task RegisterClaimAndRunAsync_WhenActiveJobAlreadyExists_DoesNotClaimOrRun()
    {
        var claimed = false;
        var executed = false;

        var act = () => SyncJobRunCoordinator.RegisterClaimAndRunAsync(
            () => Task.FromResult(Creation(Guid.NewGuid(), created: false)),
            _ => { claimed = true; return Task.FromResult<int?>(1); },
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            _ => { executed = true; return Task.FromResult(new SyncUitkomst()); },
            _ => "safe failure");

        await Assert.ThrowsAsync<SyncAlreadyRunningException>(act);
        Assert.False(claimed);
        Assert.False(executed);
    }

    private static SyncJobCreation Creation(Guid id, bool created) => new(
        new SyncJobStatus(id, "pending", -1, 0, DateTime.UtcNow, null, null, null), created);
}
