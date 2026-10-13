using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Teambegeleiding;

namespace Planner.Endpoints.Teambegeleiding;

/// <summary>
/// Gedeelde orkestratie van <c>GET /api/beheer/teambegeleiding/{team}</c> (#1701): contactgegevens ophalen
/// en de inzage vastleggen. De poort (elke ingelogde rol, #1330) zit in de tier-Function.
/// <para>
/// <b>Fail-closed.</b> De inzageregel wordt geschreven vóórdat het antwoord teruggaat. Lukt het vastleggen
/// niet, dan gooit de opslag een uitzondering en krijgt de aanroeper een 500 in plaats van de gegevens:
/// geen contactgegevens zonder inzageregel.
/// </para>
/// <para>
/// De inzageregel bevat alleen clubcode, inziener (uit het principal), team en het aantal rijen
/// (<see cref="TeambegeleidingInzageNieuw"/>); nooit naam, e-mailadres of telefoonnummer van een begeleider.
/// </para>
/// </summary>
public static class TeambegeleidingInzageEndpointCore
{
    public static async Task<IActionResult> ContactgegevensAsync(
        string clubCode, string team, TeambegeleidingInzager wie, ITeambegeleidingInzageStore store)
    {
        var contacten = await store.HaalContactgegevensAsync(clubCode, team);
        await store.LogInzageAsync(TeambegeleidingInzageNieuw.Maak(clubCode, wie, team, contacten.Count));
        return new OkObjectResult(contacten);
    }
}
