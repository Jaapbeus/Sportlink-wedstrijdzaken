using AwesomeAssertions;
using Planner.Endpoints.Health;
using Xunit;

namespace Planner.Endpoints.Tests.Health;

public class HealthStatusCacheTests
{
    [Fact]
    public async Task ConcurrentRequestsShareRefreshAndUseTierSpecificKeys()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new HealthStatusCache<string>(TimeSpan.FromSeconds(5), () => now);
        var count = 0;
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = cache.GetAsync("Postgres", () => { Interlocked.Increment(ref count); return source.Task; });
        var b = cache.GetAsync("Postgres", () => { Interlocked.Increment(ref count); return Task.FromResult("wrong"); });
        var otherTier = cache.GetAsync("SqlServer", () => { Interlocked.Increment(ref count); return Task.FromResult("sql"); });
        count.Should().Be(2);

        source.SetResult("pg");
        (await a).Should().Be("pg");
        (await b).Should().Be("pg");
        (await otherTier).Should().Be("sql");
        (await cache.GetAsync("Postgres", () => throw new InvalidOperationException())).Should().Be("pg");
        count.Should().Be(2);
    }

    [Fact]
    public async Task CallerCancellationCancelsOnlyItsWaitAndFailureIsCachedUntilTtlExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new HealthStatusCache<string>(TimeSpan.FromSeconds(5), () => now);
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var cancelledWait = cache.GetAsync("Postgres", () => { calls++; return source.Task; }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        calls.Should().Be(1);
        source.SetResult("ready");
        (await cache.GetAsync("Postgres", () => throw new InvalidOperationException())).Should().Be("ready");

        var failures = 0;
        var faultCache = new HealthStatusCache<string>(TimeSpan.FromSeconds(5), () => now);
        Func<Task<string>> failing = () => { failures++; throw new InvalidOperationException("probe failed"); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => faultCache.GetAsync("Postgres", failing));
        await Assert.ThrowsAsync<InvalidOperationException>(() => faultCache.GetAsync("Postgres", failing));
        failures.Should().Be(1);
        now += TimeSpan.FromSeconds(6);
        await Assert.ThrowsAsync<InvalidOperationException>(() => faultCache.GetAsync("Postgres", failing));
        failures.Should().Be(2);
    }

    [Fact]
    public async Task SuccessfulSnapshotRefreshesAfterFiveSecondTtl()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new HealthStatusCache<string>(TimeSpan.FromSeconds(5), () => now);
        var calls = 0;
        Task<string> Probe() => Task.FromResult($"snapshot-{++calls}");

        (await cache.GetAsync("Postgres", Probe)).Should().Be("snapshot-1");
        (await cache.GetAsync("Postgres", Probe)).Should().Be("snapshot-1");
        now += TimeSpan.FromSeconds(5);
        (await cache.GetAsync("Postgres", Probe)).Should().Be("snapshot-2");
        calls.Should().Be(2);
    }
}
