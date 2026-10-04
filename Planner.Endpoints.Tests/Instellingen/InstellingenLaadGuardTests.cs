using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Instellingen;
using Xunit;

namespace Planner.Endpoints.Tests.Instellingen;

public class InstellingenLaadGuardTests
{
    [Fact]
    public async Task TweedeAanroep_LaadtNietOpnieuw()
    {
        var n = 0;
        var g = new InstellingenLaadGuard(_ => { n++; return Task.FromResult(true); });
        await g.EnsureLoadedAsync(NullLogger.Instance);
        await g.EnsureLoadedAsync(NullLogger.Instance);
        n.Should().Be(1);
        g.IsGeladen.Should().BeTrue();
    }

    [Fact]
    public async Task ParallelleAanroepen_LadenEenmaal()
    {
        var n = 0;
        var g = new InstellingenLaadGuard(async _ => { Interlocked.Increment(ref n); await Task.Delay(50); return true; });
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => g.EnsureLoadedAsync(NullLogger.Instance)));
        n.Should().Be(1);
    }

    [Fact]
    public async Task MislukteLading_WordtOpnieuwGeprobeerd()
    {
        var n = 0;
        var g = new InstellingenLaadGuard(_ => Task.FromResult(++n >= 2));
        await g.EnsureLoadedAsync(NullLogger.Instance);
        g.IsGeladen.Should().BeFalse();
        await g.EnsureLoadedAsync(NullLogger.Instance);
        g.IsGeladen.Should().BeTrue();
        n.Should().Be(2);
    }

    [Fact]
    public async Task GooiendeLader_PropageertNietEnWordtOpnieuwGeprobeerd()
    {
        var n = 0;
        var g = new InstellingenLaadGuard(_ => { if (++n == 1) throw new InvalidOperationException("db down"); return Task.FromResult(true); });
        var act = () => g.EnsureLoadedAsync(NullLogger.Instance);
        await act.Should().NotThrowAsync();
        g.IsGeladen.Should().BeFalse();
        await g.EnsureLoadedAsync(NullLogger.Instance);
        g.IsGeladen.Should().BeTrue();
    }
}
