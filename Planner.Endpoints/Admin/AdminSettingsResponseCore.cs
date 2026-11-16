using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Scheduling;

namespace Planner.Endpoints.Admin;

/// <summary>Pure response and preview projections for the two admin database tiers.</summary>
public static class AdminSettingsResponseCore
{
    public static void VulCronPreview(Dictionary<string, object?> result)
    {
        if (result.TryGetValue("FetchSchedule", out var sched) && sched is string schedStr && !string.IsNullOrWhiteSpace(schedStr))
        {
            result["fetchScheduleLeesbaar"] = CronLeesbaar.Vertaal(schedStr);
            result["volgendeMomenten"] = CronPreviewCore.BerekenVolgendeMomenten(schedStr, 3);
        }
    }

    public static IActionResult BouwPutResponse(IEnumerable<string> gewijzigdeVelden,
        bool fetchScheduleChanged, bool herstartAutomatisch, string? herstartOpmerking, string? nieuweSchedule) =>
        new OkObjectResult(new
        {
            gewijzigdeVelden = gewijzigdeVelden.ToArray(),
            herstartVereist = fetchScheduleChanged && !herstartAutomatisch,
            herstartAutomatisch,
            opmerking = herstartOpmerking,
            fetchScheduleLeesbaar = fetchScheduleChanged && nieuweSchedule != null
                ? CronLeesbaar.Vertaal(nieuweSchedule) : null,
            volgendeMomenten = fetchScheduleChanged && nieuweSchedule != null
                ? CronPreviewCore.BerekenVolgendeMomenten(nieuweSchedule, 3) : null
        });
}
