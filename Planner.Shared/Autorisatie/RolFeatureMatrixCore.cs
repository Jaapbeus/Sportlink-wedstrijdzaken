namespace Planner.Shared.Autorisatie;

/// <summary>
/// Tier-onafhankelijke validatie voor de toegangsmatrix (#1390) — de ENE plek waar een
/// RolNaam/FeatureKey-combinatie tegen de toegestane lijsten wordt getoetst, zodat geen tierbestand
/// zijn eigen kopie van deze validatie verzint (docs/ARCHITECTUUR-CODEKWALITEIT.md regel 1, zelfde
/// precedent als <c>ThemeCore</c>/<c>FeedbackCore</c>). Framework-vrij: een
/// <c>AdminRolFeatureInstellingenFunction</c> (beide tiers) vertaalt <see cref="RolFeatureValidatieResultaat"/>
/// naar een <c>IActionResult</c>, deze klasse kent geen ASP.NET-afhankelijkheid.
/// </summary>
public static class RolFeatureMatrixCore
{
    /// <summary>Alle instelbare FeatureKeys: de Sportlink-mutatieacties plus elk menu-item.</summary>
    public static readonly IReadOnlyList<string> AlleFeatureKeys =
        Integrations.SportlinkClub.SportlinkRolFeature.Alle.Concat(MenuFeatureKeys.Alle).ToArray();

    /// <summary>Retourneert <c>null</c> als beide waarden geldig zijn, anders een leesbare foutmelding.</summary>
    public static string? Valideer(string? rolNaam, string? featureKey)
    {
        if (string.IsNullOrWhiteSpace(rolNaam) || !RolNamen.Alle.Contains(rolNaam))
            return $"Onbekende RolNaam. Toegestaan: {string.Join(", ", RolNamen.Alle)}.";

        if (string.IsNullOrWhiteSpace(featureKey) || !AlleFeatureKeys.Contains(featureKey))
            return $"Onbekende FeatureKey. Toegestaan: {string.Join(", ", AlleFeatureKeys)}.";

        return null;
    }
}
