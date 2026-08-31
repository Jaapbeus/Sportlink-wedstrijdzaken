using System.Globalization;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/SunsetCalculator.cs</c> (issue 888 vervolg,
/// §41). Bewust een tweede kopie in plaats van een verhuizing naar <c>Planner.Shared</c> — zelfde
/// afweging als <c>PostgresLeeftijdNormalisatie</c>: de berekening zelf is puur (NOAA-zonvergelijking,
/// geen SQL), maar leest de club se coördinaten uit een tier-eigen statische cache
/// (<see cref="PostgresAppSettings"/> hier, <c>SystemUtilities.AppSettings</c> op de SQL
/// Server-tier) — die twee caches verhuizen naar één gedeelde plek zou een grotere refactor zijn
/// dan deze slice rechtvaardigt.
/// </summary>
public static class PostgresSunsetCalculator
{
    private const double DefaultLatitude  = 52.1551; // Geografisch centrum NL — configureer via public.appsettings.accommodatielatitude
    private const double DefaultLongitude = 5.3878;  // Geografisch centrum NL — configureer via public.appsettings.accommodatielongitude

    private static readonly TimeZoneInfo AmsterdamTz = GetAmsterdamTimeZone();

    private static TimeZoneInfo GetAmsterdamTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"); }
    }

    private static double Latitude =>
        TryParseCoordinate(PostgresAppSettings.GetSetting("accommodatieLatitude"), DefaultLatitude);

    private static double Longitude =>
        TryParseCoordinate(PostgresAppSettings.GetSetting("accommodatieLongitude"), DefaultLongitude);

    private static double TryParseCoordinate(string? value, double fallback) =>
        !string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : fallback;

    /// <summary>Bereken zonsondergangstijd voor een bepaalde datum in lokale Amsterdam-tijd.</summary>
    public static TimeOnly GetSunset(DateOnly date)
    {
        var utcSunset = CalculateSunsetUtc(date, Latitude, Longitude);
        var localSunset = TimeZoneInfo.ConvertTimeFromUtc(utcSunset, AmsterdamTz);
        return TimeOnly.FromDateTime(localSunset);
    }

    private static DateTime CalculateSunsetUtc(DateOnly date, double lat, double lon)
    {
        int dayOfYear = date.DayOfYear;
        double latRad = lat * Math.PI / 180.0;

        double gamma = 2.0 * Math.PI / 365.0 * (dayOfYear - 1 + 0.5);

        double eqTime = 229.18 * (
            0.000075
            + 0.001868 * Math.Cos(gamma)
            - 0.032077 * Math.Sin(gamma)
            - 0.014615 * Math.Cos(2.0 * gamma)
            - 0.040849 * Math.Sin(2.0 * gamma)
        );

        double decl = 0.006918
            - 0.399912 * Math.Cos(gamma)
            + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2.0 * gamma)
            + 0.000907 * Math.Sin(2.0 * gamma)
            - 0.002697 * Math.Cos(3.0 * gamma)
            + 0.00148 * Math.Sin(3.0 * gamma);

        double zenith = 90.833 * Math.PI / 180.0;
        double cosHa = (Math.Cos(zenith) / (Math.Cos(latRad) * Math.Cos(decl)))
                     - Math.Tan(latRad) * Math.Tan(decl);

        cosHa = Math.Max(-1.0, Math.Min(1.0, cosHa));
        double ha = Math.Acos(cosHa) * 180.0 / Math.PI;

        double sunsetMinutes = 720 - 4.0 * (lon - ha) - eqTime;

        int hours = (int)(sunsetMinutes / 60.0);
        int minutes = (int)(sunsetMinutes % 60.0);
        int seconds = (int)((sunsetMinutes - hours * 60 - minutes) * 60);

        return new DateTime(date.Year, date.Month, date.Day, hours, minutes, seconds, DateTimeKind.Utc);
    }
}
