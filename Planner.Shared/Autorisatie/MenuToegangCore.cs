namespace Planner.Shared.Autorisatie;

/// <summary>
/// Tier-onafhankelijke bepaling van welke menu-items de aanroeper mag zien (#1647). De ene plek voor
/// deze regel, zodat geen tier een eigen kopie krijgt (docs/ARCHITECTUUR-CODEKWALITEIT.md regel 1).
/// Dit is UI-gemak: de autorisatie van de endpoints zelf blijft op de server (<c>AdminEndpoint</c>) leidend.
/// </summary>
public static class MenuToegangCore
{
    /// <summary>Antwoord van <c>GET planner/menu-toegang</c>: de toegestane menu-sleutels en de niet-gevoelige
    /// menuvlag "Sportlink Web Extension aan" (#1647) — zodat het menu die vlag niet uit de admin-only
    /// instellingenroute hoeft te halen.</summary>
    public sealed record MenuToegang(IReadOnlyList<string> FeatureKeys, bool SportlinkExtensieActief);

    public static async Task<MenuToegang> BepaalAsync(
        bool isAdmin,
        Func<string, bool> heeftRol,
        Func<Task<IReadOnlyDictionary<(string RolNaam, string FeatureKey), bool>>> leesMatrix,
        Func<Task<bool>> leesExtensieActief) =>
        new(await BepaalToegestaneKeysAsync(isAdmin, heeftRol, leesMatrix), await leesExtensieActief());

    /// <summary>
    /// <c>admin</c>: alle menu-sleutels, zonder de matrix te raadplegen. Anders: de unie van de sleutels
    /// die minstens één van de instelbare rollen van de aanroeper aan heeft staan, minus de uitsluitend
    /// voor admin bedoelde sleutels (<see cref="MenuFeatureKeys.AlleenAdmin"/>).
    /// </summary>
    public static async Task<IReadOnlyList<string>> BepaalToegestaneKeysAsync(
        bool isAdmin,
        Func<string, bool> heeftRol,
        Func<Task<IReadOnlyDictionary<(string RolNaam, string FeatureKey), bool>>> leesMatrix)
    {
        if (isAdmin) return MenuFeatureKeys.Alle;

        var rollen = RolNamen.Alle.Where(heeftRol).ToArray();
        if (rollen.Length == 0) return Array.Empty<string>();

        var matrix = await leesMatrix();
        return BepaalToegestaneKeys(rollen, matrix);
    }

    public static IReadOnlyList<string> BepaalToegestaneKeys(
        IReadOnlyCollection<string> rollen,
        IReadOnlyDictionary<(string RolNaam, string FeatureKey), bool> matrix) =>
        MenuFeatureKeys.Alle
            .Where(k => !MenuFeatureKeys.AlleenAdmin.Contains(k))
            .Where(k => rollen.Any(r => matrix.TryGetValue((r, k), out var aan) && aan))
            .ToArray();
}
