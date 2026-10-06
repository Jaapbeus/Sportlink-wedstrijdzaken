using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Leren;

namespace Planner.Endpoints.Leren;

/// <summary>Aansluiting van de beheer-endpoints voor de wachtrij met onbekende teamteksten (#1568 deel C).</summary>
public static class OnbekendeTeamTekstEndpointCore
{
    public const int StandaardLimit = 100;
    public const int MaxLimit = 500;

    public static async Task<IActionResult> LijstAsync(string clubCode, IQueryCollection query, IOnbekendeTeamTekstStore store)
    {
        var status = query["status"].ToString();
        if (!string.IsNullOrWhiteSpace(status) && !OnbekendeTeamTekstStatus.Alle.Contains(status))
            return new BadRequestObjectResult(new { error = "Ongeldige status. Gebruik 'open', 'afgehandeld' of 'genegeerd'." });

        var limit = int.TryParse(query["limit"].ToString(), out var l) ? Math.Min(MaxLimit, Math.Max(1, l)) : StandaardLimit;
        var items = await store.LijstAsync(clubCode, string.IsNullOrWhiteSpace(status) ? null : status, limit);
        var open = await store.AantalOpenAsync(clubCode);
        return new OkObjectResult(new { count = items.Count, limit, open, items });
    }

    /// <summary>Zet een regel op <c>open</c>, <c>afgehandeld</c> of <c>genegeerd</c>.</summary>
    public static async Task<IActionResult> ZetStatusAsync(string clubCode, int id, string? body, IOnbekendeTeamTekstStore store)
    {
        var status = TeamAliasEndpointCore.LeesBody<StatusBody>(body)?.Status;
        if (status is null || !OnbekendeTeamTekstStatus.Alle.Contains(status))
            return new BadRequestObjectResult(new { error = "Ongeldige status. Gebruik 'open', 'afgehandeld' of 'genegeerd'." });

        var rijen = await store.ZetStatusAsync(clubCode, id, status);
        return rijen == 0
            ? new NotFoundObjectResult(new { error = $"Onbekende teamtekst {id} niet gevonden." })
            : new OkObjectResult(new { id, status });
    }

    private sealed class StatusBody { public string? Status { get; set; } }
}
