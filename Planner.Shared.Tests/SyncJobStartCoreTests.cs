using Planner.Shared.Sync;
using Xunit;

namespace Planner.Shared.Tests;

public sealed class SyncJobStartCoreTests
{
    private static SyncJobStatus Job(Guid id) =>
        new(id, "pending", -1, 2, DateTime.UtcNow, null, null, null);

    [Fact]
    public async Task ExistingJob_IsReusedWithoutInsert_AndTransactionCommitsOnce()
    {
        var existing = Job(Guid.NewGuid());
        var inserts = 0;
        var commits = 0;
        var result = await SyncJobStartCore.GetOrCreateAsync(
            () => Task.FromResult<SyncJobStatus?>(existing),
            () => { inserts++; return Task.FromResult(Job(Guid.NewGuid())); },
            () => { commits++; return Task.CompletedTask; });

        Assert.Same(existing, result.Job);
        Assert.False(result.Created);
        Assert.Equal(0, inserts);
        Assert.Equal(1, commits);
    }

    [Fact]
    public async Task NoActiveJob_InsertsAndCommitsOnce()
    {
        var inserted = Job(Guid.NewGuid());
        var commits = 0;
        var result = await SyncJobStartCore.GetOrCreateAsync(
            () => Task.FromResult<SyncJobStatus?>(null),
            () => Task.FromResult(inserted),
            () => { commits++; return Task.CompletedTask; });

        Assert.Same(inserted, result.Job);
        Assert.True(result.Created);
        Assert.Equal(1, commits);
    }

    [Fact]
    public async Task InsertFailure_DoesNotCommit()
    {
        var commits = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SyncJobStartCore.GetOrCreateAsync(
            () => Task.FromResult<SyncJobStatus?>(null),
            () => throw new InvalidOperationException("insert failed"),
            () => { commits++; return Task.CompletedTask; }));
        Assert.Equal(0, commits);
    }
}
