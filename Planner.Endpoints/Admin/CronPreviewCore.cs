using Cronos;

namespace Planner.Endpoints.Admin;

/// <summary>UTC preview of the next executions of a six-field schedule.</summary>
public static class CronPreviewCore
{
    internal static List<string> BerekenVolgendeMomenten(string cron, int aantal)
    {
        var resultaten = new List<string>();
        if (!CronExpression.TryParse(cron, CronFormat.IncludeSeconds, out var expr)) return resultaten;

        var nu = DateTime.UtcNow;
        var volgende = nu;
        for (int i = 0; i < aantal; i++)
        {
            var next = expr.GetNextOccurrence(volgende, TimeZoneInfo.Utc);
            if (next == null) break;
            resultaten.Add(next.Value.ToString("yyyy-MM-ddTHH:mm:ss"));
            volgende = next.Value;
        }
        return resultaten;
    }

}
