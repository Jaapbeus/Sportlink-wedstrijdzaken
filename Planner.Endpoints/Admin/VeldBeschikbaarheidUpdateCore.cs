using Microsoft.AspNetCore.Mvc;

namespace Planner.Endpoints.Admin;

public static class VeldBeschikbaarheidUpdateCore
{
    public static IActionResult? ValideerVeldOmschrijvingen(string? naam, string? type)
    {
        if (string.IsNullOrWhiteSpace(naam))
            return new BadRequestObjectResult(new { error = "VeldNaam verplicht" });
        if (string.IsNullOrWhiteSpace(type))
            return new BadRequestObjectResult(new { error = "VeldType verplicht (vrije tekst, bijv. kunstgras of natuurgras)" });
        return null;
    }

    public static IActionResult? ValideerTijden(string? vanaf, string? tot)
    {
        if (string.IsNullOrWhiteSpace(vanaf) || !TimeSpan.TryParse(vanaf, out var vanafTijd))
            return new BadRequestObjectResult(new { error = "BeschikbaarVanaf vereist HH:mm formaat" });
        if (string.IsNullOrWhiteSpace(tot) || !TimeSpan.TryParse(tot, out var totTijd))
            return new BadRequestObjectResult(new { error = "BeschikbaarTot vereist HH:mm formaat" });
        if (totTijd <= vanafTijd)
            return new BadRequestObjectResult(new { error = "BeschikbaarTot moet na BeschikbaarVanaf liggen" });
        return null;
    }

    public static async Task<IActionResult?> ConflicterendeCombinatieAsync(Func<Task<bool>> bestaatAndere)
    {
        if (!await bestaatAndere()) return null;
        return new ConflictObjectResult(new { error = "Combinatie veld + dag + periode bestaat al" });
    }
}
