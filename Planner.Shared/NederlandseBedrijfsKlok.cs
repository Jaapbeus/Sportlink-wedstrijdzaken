namespace Planner.Shared;

/// <summary>Calendar time for Dutch business rules, independent of the host time zone.</summary>
public static class NederlandseBedrijfsKlok
{
    private static readonly TimeZoneInfo Tijdzone = VindTijdzone();

    private static TimeZoneInfo VindTijdzone() => VindTijdzone(TimeZoneInfo.FindSystemTimeZoneById);

    private static TimeZoneInfo VindTijdzone(Func<string, TimeZoneInfo> zoekTijdzone)
    {
        try { return zoekTijdzone("Europe/Amsterdam"); }
        catch (TimeZoneNotFoundException)
        {
            // Windows without ICU may only expose the Windows time-zone ID.
            return zoekTijdzone("W. Europe Standard Time");
        }
    }

    public static DateTimeOffset NaarNederland(DateTimeOffset utcNu)
    {
        VereisUtc(utcNu);
        return TimeZoneInfo.ConvertTime(utcNu, Tijdzone);
    }

    /// <summary>Pure lookup seam: missing time-zone data propagates instead of inventing a date.</summary>
    public static DateTimeOffset NaarNederland(DateTimeOffset utcNu, Func<string, TimeZoneInfo> zoekTijdzone)
    {
        ArgumentNullException.ThrowIfNull(zoekTijdzone);
        VereisUtc(utcNu);
        return TimeZoneInfo.ConvertTime(utcNu, VindTijdzone(zoekTijdzone));
    }

    private static void VereisUtc(DateTimeOffset utcNu)
    {
        if (utcNu.Offset != TimeSpan.Zero)
            throw new ArgumentException("Expected a UTC instant.", nameof(utcNu));
    }

    public static DateOnly Vandaag(DateTimeOffset utcNu, Func<string, TimeZoneInfo> zoekTijdzone) =>
        DateOnly.FromDateTime(NaarNederland(utcNu, zoekTijdzone).DateTime);

    public static DateTime Nu() => NaarNederland(DateTimeOffset.UtcNow).DateTime;

    public static DateOnly Vandaag() => DateOnly.FromDateTime(Nu());
}
