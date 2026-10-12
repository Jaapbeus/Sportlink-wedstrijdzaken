using Microsoft.AspNetCore.Mvc;
using Planner.Shared.Infrastructure;

namespace Planner.Endpoints.Teambegeleiding;

/// <summary>
/// De gedeelde toegangs- en gebruiksgrens van <c>POST /api/beheer/teambegeleiding/doorsturen</c> op
/// beide tiers (#1671): een vrij ontvangersveld is alleen voor de rol <c>admin</c>, en het aantal
/// verzendingen per gebruiker is begrensd.
/// </summary>
public static class TeambegeleidingDoorsturenEndpointCore
{
    public const int MaxPerVenster = 3;
    public static readonly TimeSpan Venster = TimeSpan.FromMinutes(10);

    private static readonly GebruikerLimiet Limiet = new(MaxPerVenster, Venster);

    /// <summary>
    /// Geeft 403 als een niet-beheerder een vrij ontvangersveld meestuurt, 429 als de limiet voor deze
    /// aanroeper is bereikt, anders <c>null</c> (en telt de aanroep mee).
    /// </summary>
    public static IActionResult? Controleer(bool isAdmin, string? vrijeOntvangers, string? limietSleutel, GebruikerLimiet? limiet = null)
    {
        if (!isAdmin && !string.IsNullOrWhiteSpace(vrijeOntvangers))
            return new ObjectResult(new { error = "Een vrij ontvangersveld is alleen beschikbaar voor beheerders." }) { StatusCode = 403 };

        if (!(limiet ?? Limiet).TryAcquire(limietSleutel))
            return new ObjectResult(new { error = "Er zijn net veel vragen doorgestuurd. Probeer het over enkele minuten nog eens." }) { StatusCode = 429 };

        return null;
    }
}
