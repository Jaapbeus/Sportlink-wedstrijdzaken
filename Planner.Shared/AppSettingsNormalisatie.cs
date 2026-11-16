namespace Planner.Shared;

/// <summary>Tier-onafhankelijke normalisatie van schrijfbare clubinstellingen.</summary>
public static class AppSettingsNormalisatie
{
    /// <summary>Leegmaken van de nullable optimalisatie-aanvang wist de instelling; overige invoer blijft intact.</summary>
    public static string? NormaliseerWaarde(string veld, string? waarde) =>
        veld.Equals("OptimalisatieAanvang", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(waarde) ? null : waarde;
}
