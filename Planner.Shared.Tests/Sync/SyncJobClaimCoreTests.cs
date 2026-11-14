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

    [Fact]
    public async Task ExecuteIfClaimedAsync_PogingVariant_GeeftHetPogingnummerDoorAanDeVerwerking()
    {
        int? verwerkt = null;

        var claimed = await SyncJobClaimCore.ExecuteIfClaimedAsync(
            () => Task.FromResult<int?>(2),
            attempt => { verwerkt = attempt; return Task.CompletedTask; });

        claimed.Should().BeTrue();
        verwerkt.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteIfClaimedAsync_PogingVariant_ZonderClaimWordtNietVerwerkt()
    {
        var verwerkt = false;

        var claimed = await SyncJobClaimCore.ExecuteIfClaimedAsync(
            () => Task.FromResult<int?>(null),
            _ => { verwerkt = true; return Task.CompletedTask; });

        claimed.Should().BeFalse();
        verwerkt.Should().BeFalse();
    }
}
