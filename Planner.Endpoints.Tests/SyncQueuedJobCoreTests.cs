using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Sync;
using Planner.Shared.Sync;
using Xunit;

namespace Planner.Endpoints.Tests;

public sealed class SyncQueuedJobCoreTests
{
    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task InvalidMessage_DoesNotClaimOrRun(string message)
    {
        var claims = 0;
        var runs = 0;
        await SyncQueuedJobCore.RunAsync(message, NullLogger.Instance,
            _ => { claims++; return Task.FromResult<int?>(1); },
            (_, _) => { runs++; return Task.CompletedTask; });
        Assert.Equal(0, claims);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task DuplicateMessage_IsNotProcessedAfterClaimFails()
    {
        var job = new SyncJobMessage { JobId = Guid.NewGuid(), WeekOffsetFrom = -1, WeekOffsetTo = 3 };
        var runs = 0;
        await SyncQueuedJobCore.RunAsync(JsonSerializer.Serialize(job), NullLogger.Instance,
            _ => Task.FromResult<int?>(null),
            (_, _) => { runs++; return Task.CompletedTask; });
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ClaimedMessage_PreservesWindowAndAttempt()
    {
        var job = new SyncJobMessage { JobId = Guid.NewGuid(), WeekOffsetFrom = -1, WeekOffsetTo = 3 };
        var runs = 0;
        await SyncQueuedJobCore.RunAsync(JsonSerializer.Serialize(job), NullLogger.Instance,
            id => { Assert.Equal(job.JobId, id); return Task.FromResult<int?>(2); },
            (received, attempt) =>
            {
                Assert.Equal(job.JobId, received.JobId);
                Assert.Equal((-1, 3), (received.WeekOffsetFrom, received.WeekOffsetTo));
                Assert.Equal(2, attempt);
                runs++;
                return Task.CompletedTask;
            });
        Assert.Equal(1, runs);
    }
}
