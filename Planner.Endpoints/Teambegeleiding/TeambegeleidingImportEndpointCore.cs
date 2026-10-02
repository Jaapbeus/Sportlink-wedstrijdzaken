using Microsoft.AspNetCore.Mvc;
using Planner.Shared;

namespace Planner.Endpoints.Teambegeleiding;

/// <summary>
/// De gedeelde 400-vertaling van <see cref="TeambegeleidingCsv.ParseEnValideer"/> voor
/// <c>POST /api/beheer/teambegeleiding/import</c> op beide tiers (#1461). Eén plek, zodat een tier
/// de lengtevalidatie niet meer kan vergeten (de Postgres-tier gaf daardoor een 500).
/// </summary>
public static class TeambegeleidingImportEndpointCore
{
    /// <summary>Geeft de 400-respons als de CSV ongeldig is of een kolom te lang, anders <c>null</c>.</summary>
    public static IActionResult? Weiger(TeambegeleidingCsvResultaat resultaat)
    {
        if (!resultaat.IsValid)
            return new BadRequestObjectResult(new { error = resultaat.Error, ontbreekt = resultaat.Ontbreekt });

        // #1131/#1461: kolomgrenzen valideren VOORDAT er iets destructiefs gebeurt (DELETE/INSERT).
        if (resultaat.Lengtefouten.Count > 0)
            return new BadRequestObjectResult(new
            {
                error = "Een of meer rijen overschrijden de maximale kolomlengte. De vorige import is niet gewijzigd.",
                fouten = resultaat.Lengtefouten
            });

        return null;
    }
}
