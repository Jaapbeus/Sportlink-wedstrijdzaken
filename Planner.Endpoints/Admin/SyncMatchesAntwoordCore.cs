using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Sync;

namespace Planner.Endpoints.Admin;

/// <summary>
/// Gedeeld antwoord van de handmatige sync-route op beide tiers (<c>/sync-matches</c> en
/// <c>/postgres/sync-matches</c>, #1081, #1779). Eén plek voor de keuze tussen <c>200</c> en
/// <c>207</c>, zodat de tiers niet uit de pas kunnen lopen.
/// </summary>
public static class SyncMatchesAntwoordCore
{
    public const string GedeeltelijkMisluktMelding =
        "Eén of meer deelstappen zijn mislukt; lastsynctimestamp is niet bijgewerkt. "
        + "Zie het functielog voor de betrokken fase(s).";

    /// <summary>
    /// <c>200</c> met <paramref name="geslaagdTekst"/> als alle fases slaagden; anders <c>207</c>
    /// met de mislukte fases. 200 zou "klaar" betekenen terwijl de laatste-sync-tijd bewust niet is
    /// bijgewerkt; 207 maakt het verschil zichtbaar zonder de geslaagde deelstappen weg te gooien.
    /// </summary>
    public static IActionResult Bepaal(SyncUitkomst uitkomst, int weekOffsetFrom, int weekOffsetTo, string geslaagdTekst)
    {
        if (!uitkomst.GedeeltelijkMislukt)
            return new OkObjectResult(geslaagdTekst);

        return new ObjectResult(new
        {
            status = "gedeeltelijk mislukt",
            weekOffsetFrom,
            weekOffsetTo,
            mislukteFases = uitkomst.MislukteFases,
            melding = GedeeltelijkMisluktMelding
        })
        { StatusCode = StatusCodes.Status207MultiStatus };
    }
}
