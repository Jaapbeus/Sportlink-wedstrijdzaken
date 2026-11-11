using AwesomeAssertions;
using Xunit;

namespace Planner.Shared.Tests;

public sealed class NederlandseBedrijfsKlokLookupTests
{
    private static readonly DateTimeOffset BoundaryUtc = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo SummerZone = TimeZoneInfo.CreateCustomTimeZone("test-dutch-summer", TimeSpan.FromHours(2), "test", "test");

    [Fact]
    public void Vandaag_BothTimeZonesMissing_ThrowsWithoutInventingTomorrow()
    {
        var lookups = new List<string>();
        Action clock = () => NederlandseBedrijfsKlok.Vandaag(BoundaryUtc, id =>
        {
            lookups.Add(id);
            throw new TimeZoneNotFoundException();
        });
        clock.Should().Throw<TimeZoneNotFoundException>();
        lookups.Should().Equal("Europe/Amsterdam", "W. Europe Standard Time");
    }

    [Fact]
    public void Vandaag_UsesWindowsFallbackAndKeepsActualCalendarDay()
    {
        var lookups = new List<string>();
        var today = NederlandseBedrijfsKlok.Vandaag(BoundaryUtc, id =>
        {
            lookups.Add(id);
            return id == "Europe/Amsterdam" ? throw new TimeZoneNotFoundException() : SummerZone;
        });
        today.Should().Be(new DateOnly(2026, 6, 30));
        lookups.Should().Equal("Europe/Amsterdam", "W. Europe Standard Time");
    }

    [Fact]
    public void Vandaag_IanaZoneAvailable_DoesNotConsultWindows()
    {
        var lookups = new List<string>();
        var today = NederlandseBedrijfsKlok.Vandaag(BoundaryUtc, id => { lookups.Add(id); return SummerZone; });
        today.Should().Be(new DateOnly(2026, 6, 30));
        lookups.Should().Equal("Europe/Amsterdam");
    }

    [Fact]
    public void NaarNederland_RejectsNonUtcBeforeLookup()
    {
        var lookups = 0;
        Action clock = () => NederlandseBedrijfsKlok.NaarNederland(BoundaryUtc.ToOffset(TimeSpan.FromHours(2)), _ =>
        { lookups++; return SummerZone; });
        clock.Should().Throw<ArgumentException>();
        lookups.Should().Be(0);
    }

    [Fact]
    public void Vandaag_InvalidZoneData_StillFailsClosed()
    {
        Action clock = () => NederlandseBedrijfsKlok.Vandaag(BoundaryUtc, _ => throw new InvalidTimeZoneException());
        clock.Should().Throw<InvalidTimeZoneException>();
    }
}
