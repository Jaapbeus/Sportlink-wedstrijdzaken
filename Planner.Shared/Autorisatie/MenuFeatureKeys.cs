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

    /// <summary>#1647: de pagina "App instellingen" (voorheen "Instellingen") in de groep Admin.</summary>
    public const string InstellingenApp = "menu.instellingen.app";

    /// <summary>Alle instelbare menu-FeatureKeys, in de volgorde waarin de matrix ze als rij toont.</summary>
    public static readonly IReadOnlyList<string> Alle = new[]
    {
        Dashboard, Teambegeleiding, Planning, VeldOptimalisatie, Leermomenten, Teamaliassen, EmailTester,
        Wijzigingsverzoeken, WedstrijdAanmaken,
        InstellingenSpeeltijden, InstellingenVelden, InstellingenBegeleidingImport, InstellingenVoorkeurstijden,
        InstellingenEmailTemplates, InstellingenThema, InstellingenSportlinkExtensie, InstellingenApp
    };

    /// <summary>
    /// #1647: de groep DEBUG (Email-tester, AI-leermomenten, Feedback, E-maillog) is uitsluitend voor
    /// <c>admin</c>. Deze sleutels blijven geldig in de matrix (bestaande rijen), maar geven nooit een
    /// niet-admin zichtbaarheid: <see cref="MenuToegangCore"/> negeert ze voor elke andere rol.
    /// </summary>
    public static readonly IReadOnlyList<string> AlleenAdmin = new[] { EmailTester, Leermomenten };

    /// <summary>
    /// #1647: standaardwaarde (zonder opgeslagen rij) voor de hoofditems die vóór de menuherindeling voor
    /// elke ingelogde gebruiker zichtbaar waren. Zonder deze standaard zou de fail-closed matrix na uitrol
    /// het hele menu van alle niet-admins verbergen tot een beheerder het per rol aanzet. De groepen
    /// WZ Instellingen en Admin staan standaard uit voor niet-admins (zij worden bewust beperkt).
    /// Alleen menu-zichtbaarheid; de autorisatie van de endpoints zelf is hier niet van afhankelijk.
    /// </summary>
    public static readonly IReadOnlyList<string> StandaardAan = new[]
    {
        Dashboard, Teambegeleiding, Planning, VeldOptimalisatie, Wijzigingsverzoeken, WedstrijdAanmaken
    };
}
