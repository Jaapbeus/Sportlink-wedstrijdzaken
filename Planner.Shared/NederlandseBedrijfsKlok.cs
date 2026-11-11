namespace Planner.Shared;

/// <summary>Calendar time for Dutch business rules, independent of the host time zone.</summary>
public static class NederlandseBedrijfsKlok
{
    private static readonly TimeZoneInfo Tijdzone = VindTijdzone();

    private static TimeZoneInfo VindTijdzone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"); }
        catch (TimeZoneNotFoundException)
        {
            // Windows without ICU may only expose the Windows time-zone ID.
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }

    public static DateTimeOffset NaarNederland(DateTimeOffset utcNu)
    {
        if (utcNu.Offset != TimeSpan.Zero)
            throw new ArgumentException("Expected a UTC instant.", nameof(utcNu));
        return TimeZoneInfo.ConvertTime(utcNu, Tijdzone);
    }

    public static DateTime Nu() => NaarNederland(DateTimeOffset.UtcNow).DateTime;

    public static DateOnly Vandaag() => DateOnly.FromDateTime(Nu());
}
