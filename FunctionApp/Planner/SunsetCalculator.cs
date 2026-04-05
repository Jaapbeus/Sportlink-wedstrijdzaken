using System;

namespace SportlinkFunction.Planner
{
    /// <summary>
    /// NOAA Zonnecalculator voor zonsondergangstijden op Sportpark Spitsbergen, Veenendaal.
    /// Gebaseerd op: https://gml.noaa.gov/grad/solcalc/solareqns.PDF
    /// </summary>
    public static class SunsetCalculator
    {
        private const double Latitude = 52.0284;
        private const double Longitude = 5.5579;

        private static readonly TimeZoneInfo AmsterdamTz = GetAmsterdamTimeZone();

        private static TimeZoneInfo GetAmsterdamTimeZone()
        {
            // Windows gebruikt "W. Europe Standard Time", Linux gebruikt "Europe/Amsterdam"
            try { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"); }
        }

        /// <summary>
        /// Bereken zonsondergangstijd voor een bepaalde datum in lokale Amsterdam-tijd.
        /// </summary>
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

            // Fractional year (gamma) in radians
            double gamma = 2.0 * Math.PI / 365.0 * (dayOfYear - 1 + 0.5);

            // Equation of time (minutes)
            double eqTime = 229.18 * (
                0.000075
                + 0.001868 * Math.Cos(gamma)
                - 0.032077 * Math.Sin(gamma)
                - 0.014615 * Math.Cos(2.0 * gamma)
                - 0.040849 * Math.Sin(2.0 * gamma)
            );

            // Solar declination (radians)
            double decl = 0.006918
                - 0.399912 * Math.Cos(gamma)
                + 0.070257 * Math.Sin(gamma)
                - 0.006758 * Math.Cos(2.0 * gamma)
                + 0.000907 * Math.Sin(2.0 * gamma)
                - 0.002697 * Math.Cos(3.0 * gamma)
                + 0.00148 * Math.Sin(3.0 * gamma);

            // Hour angle for sunset (degrees)
            // Using standard atmospheric refraction correction (-0.833 degrees)
            double zenith = 90.833 * Math.PI / 180.0;
            double cosHa = (Math.Cos(zenith) / (Math.Cos(latRad) * Math.Cos(decl)))
                         - Math.Tan(latRad) * Math.Tan(decl);

            // Clamp for polar regions (shouldn't happen for Netherlands)
            cosHa = Math.Max(-1.0, Math.Min(1.0, cosHa));
            double ha = Math.Acos(cosHa) * 180.0 / Math.PI; // positive for sunset

            // Sunset time in minutes from midnight UTC
            // NOAA formula: sunset = 720 - 4*(longitude - ha) - eqtime
            double sunsetMinutes = 720 - 4.0 * (lon - ha) - eqTime;

            int hours = (int)(sunsetMinutes / 60.0);
            int minutes = (int)(sunsetMinutes % 60.0);
            int seconds = (int)((sunsetMinutes - hours * 60 - minutes) * 60);

            return new DateTime(date.Year, date.Month, date.Day, hours, minutes, seconds, DateTimeKind.Utc);
        }
    }
}
