using System.Globalization;

namespace Planner.Shared;

/// <summary>Tier-onafhankelijke normalisatie van schrijfbare clubinstellingen.</summary>
public static class AppSettingsNormalisatie
{
    /// <summary>Leegmaken van de nullable optimalisatie-aanvang wist de instelling; overige invoer blijft intact.</summary>
    public static string? NormaliseerWaarde(string veld, string? waarde) =>
        veld.Equals("OptimalisatieAanvang", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(waarde) ? null : waarde;

    /// <summary>Bewaar de oude optimalisatie-aanvang in hetzelfde HH:mm-formaat als de nieuwe waarde.</summary>
    public static string? FormatteerAuditWaarde(string veld, object? waarde)
    {
        if (waarde is null or DBNull) return null;
        if (!veld.Equals("OptimalisatieAanvang", StringComparison.OrdinalIgnoreCase))
            return waarde.ToString();

        return waarde switch
        {
            TimeOnly tijd => tijd.ToString("HH:mm", CultureInfo.InvariantCulture),
            TimeSpan tijd => TimeOnly.FromTimeSpan(tijd).ToString("HH:mm", CultureInfo.InvariantCulture),
            _ => waarde.ToString()
        };
    }
}
