using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Planner.Shared;

/// <summary>Tier-onafhankelijke normalisatie van schrijfbare clubinstellingen.</summary>
public static class AppSettingsNormalisatie
{
    /// <summary>Filtert en normaliseert alleen toegestane adminvelden, zonder waarden te loggen.</summary>
    public static Dictionary<string, string?> FilterToegestaneVelden(
        IReadOnlyDictionary<string, string?>? velden, IReadOnlyCollection<string> toegestaan, ILogger log)
    {
        var changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (velden is null) return changes;
        foreach (var (key, value) in velden)
        {
            if (!toegestaan.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                log.LogWarning("AdminSettingsPut: veld {Veld} niet in witte lijst, wordt genegeerd", LogWaarde.Schoon(key));
                continue;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z][A-Za-z0-9_]*$"))
            {
                log.LogWarning("AdminSettingsPut: veldnaam {Veld} bevat ongeldige tekens, wordt genegeerd", LogWaarde.Schoon(key));
                continue;
            }
            changes[key] = NormaliseerWaarde(key, value);
        }
        return changes;
    }

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
