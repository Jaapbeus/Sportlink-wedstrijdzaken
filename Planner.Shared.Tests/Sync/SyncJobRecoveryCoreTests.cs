using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests.Sync;

public class SyncJobRecoveryCoreTests
{
    private static readonly DateTime Nu = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private static SyncJobRecoveryItem Item() => new(Guid.NewGuid(), "club", 0, 1);

    [Fact]
    public async Task RunAsync_GeeftDeDrempelUitHetBeleidDoorEnZetElkeJobOpDeQueue()
    {
        DateTime? cutoff = null;
        var verstuurd = new List<Guid>();
        var a = Item();
        var b = Item();

        var resultaat = await SyncJobRecoveryCore.RunAsync(
            Nu,
            c => { cutoff = c; return Task.FromResult(new SyncJobRecoveryBatch([a, b], GaveUp: 1)); },
            item => { verstuurd.Add(item.JobId); return Task.CompletedTask; },
            NullLogger.Instance);

        cutoff.Should().Be(Nu.AddMinutes(-30));
        verstuurd.Should().Equal(a.JobId, b.JobId);
        resultaat.Should().Be((Requeued: 2, EnqueueFailed: 0, GaveUp: 1));
    }

    [Fact]
    public async Task RunAsync_MislukteEnqueueStoptDeRestNiet_EnWordtGeteld()
    {
        var a = Item();
        var b = Item();
        var verstuurd = new List<Guid>();

        var resultaat = await SyncJobRecoveryCore.RunAsync(
            Nu,
            _ => Task.FromResult(new SyncJobRecoveryBatch([a, b], GaveUp: 0)),
            item =>
            {
                if (item == a) throw new InvalidOperationException("queue weg");
                verstuurd.Add(item.JobId);
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        verstuurd.Should().Equal(b.JobId);
        resultaat.Should().Be((Requeued: 1, EnqueueFailed: 1, GaveUp: 0));
    }

    [Fact]
    public async Task RunAsync_ZonderOnderbrokenJobs_DoetNiets()
    {
        var resultaat = await SyncJobRecoveryCore.RunAsync(
            Nu,
            _ => Task.FromResult(new SyncJobRecoveryBatch([], GaveUp: 0)),
            _ => throw new InvalidOperationException("mag niet"),
            NullLogger.Instance);

        resultaat.Should().Be((Requeued: 0, EnqueueFailed: 0, GaveUp: 0));
    }
}
