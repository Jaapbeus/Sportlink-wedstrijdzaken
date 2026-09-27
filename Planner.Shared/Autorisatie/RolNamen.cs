namespace Planner.Shared.Autorisatie;

/// <summary>
/// De rollen die per club instelbaar zijn in de toegangsmatrix (#1390). <c>admin</c> staat hier
/// bewust niet in: admin heeft altijd volledige toegang en is nooit instelbaar, dus toont geen
/// kolom in de matrix (zie <c>RolFeatureInstellingenRepository</c>/<c>AdminRolFeatureInstellingenFunction</c>).
/// </summary>
public static class RolNamen
{
    public const string User = "user";
    public const string Wedstrijdzaken = "Wedstrijdzaken";
    public const string Sectiehoofd = "Sectiehoofd";
    public const string Ledenadministratie = "Ledenadministratie";

    /// <summary>Alle instelbare rollen, in de volgorde waarin de matrix ze als kolom toont.</summary>
    public static readonly IReadOnlyList<string> Alle = new[] { User, Wedstrijdzaken, Sectiehoofd, Ledenadministratie };
}
