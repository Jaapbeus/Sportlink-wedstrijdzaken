using AwesomeAssertions;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Shared.Tests.Email;

public class NederlandseBedrijfsKlokTests
{
    [Theory]
    [InlineData(2026, 1, 15, 22, 30, 2026, 1, 15, 23, 30)]
    [InlineData(2026, 1, 15, 23, 30, 2026, 1, 16, 0, 30)]
    [InlineData(2026, 7, 15, 21, 30, 2026, 7, 15, 23, 30)]
    [InlineData(2026, 7, 15, 22, 30, 2026, 7, 16, 0, 30)]
    public void NaarNederland_UsesWinterAndSummerOffsetsAcrossMidnight(
        int utcYear, int utcMonth, int utcDay, int utcHour, int utcMinute,
        int nlYear, int nlMonth, int nlDay, int nlHour, int nlMinute)
    {
        var utc = new DateTimeOffset(utcYear, utcMonth, utcDay, utcHour, utcMinute, 0, TimeSpan.Zero);
        var local = NederlandseBedrijfsKlok.NaarNederland(utc);

        local.DateTime.Should().Be(new DateTime(nlYear, nlMonth, nlDay, nlHour, nlMinute, 0));
    }

    [Theory]
    [InlineData(2026, 3, 29, 0, 30, 1, 30, 1)]
    [InlineData(2026, 3, 29, 1, 30, 3, 30, 2)]
    [InlineData(2026, 10, 25, 0, 30, 2, 30, 2)]
    [InlineData(2026, 10, 25, 1, 30, 2, 30, 1)]
    public void NaarNederland_UsesCorrectDstTransition(
        int year, int month, int day, int utcHour, int utcMinute,
        int nlHour, int nlMinute, int offsetHours)
    {
        var utc = new DateTimeOffset(year, month, day, utcHour, utcMinute, 0, TimeSpan.Zero);
        var local = NederlandseBedrijfsKlok.NaarNederland(utc);

        local.Hour.Should().Be(nlHour);
        local.Minute.Should().Be(nlMinute);
        local.Offset.Should().Be(TimeSpan.FromHours(offsetHours));
    }

    [Fact]
    public void NaarNederland_RequiresUtcInput()
    {
        var otherOffset = new DateTimeOffset(2026, 7, 15, 23, 30, 0, TimeSpan.FromHours(2));
        Action convert = () => NederlandseBedrijfsKlok.NaarNederland(otherOffset);
        convert.Should().Throw<ArgumentException>();
    }
}
