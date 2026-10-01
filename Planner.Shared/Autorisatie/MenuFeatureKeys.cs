namespace Planner.Shared.Autorisatie;

/// <summary>
/// FeatureKey-constanten voor de per-club, per-rol instelbare zichtbaarheid van Admin GUI-menu-items
/// (#1390). Naast <c>SportlinkRolFeature</c> (dat blijft uitsluitend over de 3 Sportlink-mutatieacties
/// gaan) — samen vormen ze de volledige rijen-lijst van de toegangsmatrix. De instellingenpagina
/// zelf ("Rechten per rol") heeft bewust geen eigen FeatureKey: die blijft hardcoded admin-only,
/// anders zou een rol zichzelf meer rechten kunnen geven.
/// </summary>
public static class MenuFeatureKeys
{
    public const string Dashboard = "menu.dashboard";
    public const string Teambegeleiding = "menu.teambegeleiding";
    public const string Planning = "menu.planning";
    public const string VeldOptimalisatie = "menu.veldoptimalisatie";
    public const string Leermomenten = "menu.leermomenten";
    public const string Teamaliassen = "menu.teamaliassen";
    public const string EmailTester = "menu.emailtester";
    public const string Wijzigingsverzoeken = "menu.wijzigingsverzoeken";
    public const string WedstrijdAanmaken = "menu.wedstrijdaanmaken";
    public const string InstellingenSpeeltijden = "menu.instellingen.speeltijden";
    public const string InstellingenVelden = "menu.instellingen.velden";
    public const string InstellingenBegeleidingImport = "menu.instellingen.begeleidingimport";
    public const string InstellingenVoorkeurstijden = "menu.instellingen.voorkeurstijden";
    public const string InstellingenEmailTemplates = "menu.instellingen.emailtemplates";
    public const string InstellingenThema = "menu.instellingen.thema";
    public const string InstellingenSportlinkExtensie = "menu.instellingen.sportlinkextensie";

    /// <summary>Alle instelbare menu-FeatureKeys, in de volgorde waarin de matrix ze als rij toont.</summary>
    public static readonly IReadOnlyList<string> Alle = new[]
    {
        Dashboard, Teambegeleiding, Planning, VeldOptimalisatie, Leermomenten, Teamaliassen, EmailTester,
        Wijzigingsverzoeken, WedstrijdAanmaken,
        InstellingenSpeeltijden, InstellingenVelden, InstellingenBegeleidingImport, InstellingenVoorkeurstijden,
        InstellingenEmailTemplates, InstellingenThema, InstellingenSportlinkExtensie
    };
}
