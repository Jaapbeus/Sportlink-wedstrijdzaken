using AwesomeAssertions;
using Planner.Shared.Sync;
using SportlinkFunction.Sync;
using Xunit;

namespace FunctionApp.Tests.Sync;

public sealed class SqlServerSeasonClockTests
{
    [Fact]
    public void SeasonDate_UnavailableZones_DoesNotInventTomorrowAtRolloverBoundary()
    {
        var utc = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        TimeZoneInfo Missing(string _) => throw new TimeZoneNotFoundException();
        ReconciliatieOndergrens.VandaagInNederland(utc.UtcDateTime, Missing).Should().Be(new DateOnly(2026, 7, 1));
        Action season = () => SqlServerSeasonRollover.BepaalVandaag(utc, Missing);
        season.Should().Throw<TimeZoneNotFoundException>();
    }
}
