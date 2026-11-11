using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Autorisatie;

namespace Planner.Endpoints.Autorisatie;

internal static class PlannerAanvragerEndpointCore
{
    public static IActionResult? Apply(string? requester, Action<string> zetAanvrager)
    {
        if (requester is null)
            return new ObjectResult(new { error = "Aanvrager kan niet worden bepaald." }) { StatusCode = 403 };

        zetAanvrager(requester);
        return null;
    }
}
