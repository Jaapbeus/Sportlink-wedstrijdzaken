using FunctionApp.Postgres.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Sportlink;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Past de Sportlink-veldplanner toe op de Planning (#1563): veld, starttijd en blokduur komen uit Sportlink als die
/// bereikbaar is; wat Sportlink niet kent behoudt de eigen berekening. Gedeelde logica staat in
/// <see cref="VeldplannerOverlayCore"/>; hier alleen de tier-eigen toepassing op <see cref="VeldbezettingItem"/>.
/// </summary>
internal static class VeldbezettingSportlinkOverlay
{
    internal static async Task<List<VeldbezettingItem>> PasToeAsync(
        List<VeldbezettingItem> items, FunctionContext context, DateOnly datum, string clubCode, ILogger log)
    {
        var koppeling = await VeldplannerOverlayCore.KoppelAsync(items.Select(i => (i.Wedstrijd, i.AanvangsTijd)).ToList(),
            clubCode, context, PostgresAppSettings.GetSetting, EgressGuard.ExternalIntegrationsAllowed, datum, log);

        return koppeling.Count == 0 ? items : items
            .Select((w, i) => koppeling.TryGetValue(i, out var b)
                ? w with { AanvangsTijd = b.StartTijd, Veld = b.Veld, DuurMinuten = b.DuurMinuten, Veldafmeting = b.Veldafmeting }
                : w)
            .OrderBy(w => VeldplannerOverlayCore.SorteerSleutel(w.AanvangsTijd))
            .ToList();
    }
}
