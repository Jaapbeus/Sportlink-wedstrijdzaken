using AwesomeAssertions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

public class SyncJobClaimCoreTests
{
    [Fact]
    public async Task ExecuteIfClaimedAsync_LosingClaim_DoesNotProcess()
    {
        var processCount = 0;
        var jobId = Guid.NewGuid();

        var claimed = await SyncJobClaimCore.ExecuteIfClaimedAsync(
            jobId,
            _ => Task.FromResult(false),
            () =>
            {
                processCount++;
                return Task.CompletedTask;
            });

        claimed.Should().BeFalse();
        processCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteIfClaimedAsync_ConcurrentDuplicate_ProcessesOnlyWinner()
    {
        var claimTaken = 0;
        var processCount = 0;
        var jobId = Guid.NewGuid();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> WorkerAsync()
        {
            await start.Task;
            return await SyncJobClaimCore.ExecuteIfClaimedAsync(
                jobId,
                _ => Task.FromResult(Interlocked.CompareExchange(ref claimTaken, 1, 0) == 0),
                () =>
                {
                    Interlocked.Increment(ref processCount);
                    return Task.CompletedTask;
                });
        }

        var first = WorkerAsync();
        var second = WorkerAsync();
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Count(claimed => claimed).Should().Be(1);
        processCount.Should().Be(1);
    }
}
