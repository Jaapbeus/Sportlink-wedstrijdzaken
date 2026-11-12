using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Health;
using Xunit;

namespace Planner.Endpoints.Tests.Health;

/// <summary>#1911: het oordeel van de health-leesproef, gedeeld door de SQL Server- en Postgres-tier.</summary>
public class HealthLeesproefTests
{
    [Fact]
    public async Task GezondeProbe_GeeftTrue()
        => (await HealthLeesproef.UitvoerenAsync("online", _ => Task.FromResult<bool?>(true), NullLogger.Instance))
            .Should().BeTrue();

    [Fact]
    public async Task GeforceerdeLookupfout_GeeftFalse()
        => (await HealthLeesproef.UitvoerenAsync(
                "online", _ => throw new InvalidCastException("TimeOnly naar TimeSpan"), NullLogger.Instance))
            .Should().BeFalse("een kapot leespad moet de status degraded maken en de deploy-smoke-test laten falen");

    [Fact]
    public async Task ProbeDieNietKlaarKomt_GeeftFalseNaTijdslimiet()
        => (await HealthLeesproef.UitvoerenAsync(
                "online", ct => Task.Delay(Timeout.Infinite, ct).ContinueWith<bool?>(_ => true, TaskContinuationOptions.NotOnCanceled),
                NullLogger.Instance, TimeSpan.FromMilliseconds(50)))
            .Should().BeFalse();

    [Fact]
    public async Task GeenRijOmTeLezen_GeeftNull()
        => (await HealthLeesproef.UitvoerenAsync("online", _ => Task.FromResult<bool?>(null), NullLogger.Instance))
            .Should().BeNull();

    [Theory]
    [InlineData("unavailable")]
    [InlineData("timeout")]
    [InlineData("unconfigured")]
    public async Task DatabaseNietOnline_MeetNietEnRoeptDeProbeNietAan(string status)
    {
        var aangeroepen = false;
        var uitkomst = await HealthLeesproef.UitvoerenAsync(
            status, _ => { aangeroepen = true; return Task.FromResult<bool?>(true); }, NullLogger.Instance);

        uitkomst.Should().BeNull();
        aangeroepen.Should().BeFalse();
    }
}
