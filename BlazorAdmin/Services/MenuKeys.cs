namespace BlazorAdmin.Services;

/// <summary>
/// De menu-FeatureKeys van "Rechten per rol" (#1390/#1647). BlazorAdmin heeft geen projectreferentie naar
/// <c>Planner.Shared</c>; dit zijn letterlijke kopieën van <c>Planner.Shared.Autorisatie.MenuFeatureKeys</c> —
/// bij een wijziging daar moeten deze meegroeien (de matrixpagina en het menu delen ze).
/// </summary>
public static class MenuKeys
{
    public const string Dashboard = "menu.dashboard";
    public const string Teambegeleiding = "menu.teambegeleiding";
    public const string Planning = "menu.planning";
    public const string VeldOptimalisatie = "menu.veldoptimalisatie";
    public const string Teamaliassen = "menu.teamaliassen";
    public const string Wijzigingsverzoeken = "menu.wijzigingsverzoeken";
    public const string WedstrijdAanmaken = "menu.wedstrijdaanmaken";
    public const string Speeltijden = "menu.instellingen.speeltijden";
    public const string Velden = "menu.instellingen.velden";
    public const string BegeleidingImport = "menu.instellingen.begeleidingimport";
    public const string Voorkeurstijden = "menu.instellingen.voorkeurstijden";
    public const string EmailTemplates = "menu.instellingen.emailtemplates";
    public const string Thema = "menu.instellingen.thema";
    public const string SportlinkExtensie = "menu.instellingen.sportlinkextensie";
    public const string AppInstellingen = "menu.instellingen.app";
}
