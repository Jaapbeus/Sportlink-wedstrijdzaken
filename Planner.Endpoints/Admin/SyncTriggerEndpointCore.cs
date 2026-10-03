using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Sync;

namespace Planner.Endpoints.Admin;

/// <summary>Uitkomst van <see cref="SyncTriggerEndpointCore.BepaalVensterAsync"/>: óf een 400-respons, óf het sync-venster.</summary>
public sealed record SyncVenster(IActionResult? Fout, int Van, int Tot, bool IsReset);

/// <summary>
/// Gedeelde orkestratie van <c>POST beheer/sync/trigger</c> (#1492): body lezen en valideren (400), het
/// tier-specifieke seizoenseinde ophalen, de "van"-weekoffset bepalen (400 bij onbekend seizoen, #1461).
/// De tiers leveren alleen de twee databasevragen aan en doen daarna de job + queue.
/// </summary>
public static class SyncTriggerEndpointCore
{
    public static async Task<SyncVenster> BepaalVensterAsync(
        string? body, int huidigJaar, Func<Task<int>> seizoensEindeWeekOffset, Func<int, Task<int?>> seizoensStartWeekOffset)
    {
        var keuze = SyncTriggerCore.LeesEnValideer(body, huidigJaar);
        if (!keuze.Geldig)
            return new SyncVenster(new BadRequestObjectResult(new { error = keuze.Fout }), 0, 0, false);

        var tot = await seizoensEindeWeekOffset();
        var (van, seizoenFout) = await SyncTriggerCore.BepaalVanWeekOffsetAsync(keuze, seizoensStartWeekOffset);
        return van is int v
            ? new SyncVenster(null, v, tot, keuze.SeasonStartYear is not null)
            : new SyncVenster(new BadRequestObjectResult(new { error = seizoenFout }), 0, 0, false);
    }
}
