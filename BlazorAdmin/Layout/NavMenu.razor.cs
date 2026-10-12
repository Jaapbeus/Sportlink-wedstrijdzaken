using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace BlazorAdmin.Layout;

/// <summary>
/// Code-behind van het hoofdmenu (#1647). Zichtbaarheid per item: <see cref="MenuZichtbaarheid"/>
/// (admin = alles, anders de sleutels uit "Rechten per rol"), gecombineerd met de bestaande vlag van de
/// Sportlink Web Extension. Sleutels en extensievlag komen via <see cref="MenuStandLader"/> uit
/// <c>GET api/planner/menu-toegang</c> (open voor elke ingelogde rol); het menu leest dus de admin-only
/// instellingen niet meer, en <c>GET api/beheer/settings</c> start alleen nog vanuit de pagina's (#1599).
/// </summary>
public partial class NavMenu : IDisposable
{
    internal const string GroepDebug = "debug";
    internal const string GroepWz = "wz";
    internal const string GroepAdmin = "admin";

    // Routes (zonder leidende slash, exact) waarbij de groep vanzelf openklapt.
    private static readonly IReadOnlyDictionary<string, string[]> GroepRoutes = new Dictionary<string, string[]>
    {
        [GroepDebug] = ["email-tester", "leermomenten", "feedback", "email-log"],
        [GroepWz] = ["instellingen/speeltijden", "instellingen/velden", "voorkeurstijden"],
        [GroepAdmin] = ["instellingen", "sportlink-extension-settings", "instellingen/teambegeleiding-import",
            "email-templates", "sportlink-rol-instellingen", "teamaliassen", "instellingen/thema"],
    };

    [Inject] private IAuthService Auth { get; set; } = default!;
    [Inject] private MenuStandLader MenuStand { get; set; } = default!;
    [Inject] private ThemeService ThemeService { get; set; } = default!;
    [Inject] private ClubSelectorService ClubSelector { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private bool collapseNavMenu = true;
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);

    private bool IsTestmodus =>
        string.Equals(ClubSelector.SelectedClubCode, "ALLSTARS", StringComparison.OrdinalIgnoreCase);

    private string? NavMenuCssClass => collapseNavMenu ? "collapse" : null;

    private MenuZichtbaarheid Zichtbaar => new(Auth.IsAdmin, MenuStand.Sleutels);

    private bool ToonWzInstellingen =>
        Zichtbaar.MagEen(MenuKeys.Speeltijden, MenuKeys.Velden, MenuKeys.Voorkeurstijden);

    // "Rechten per rol" telt mee voor een beheerder; de overige items volgen de matrix.
    private bool ToonAdmin =>
        Auth.IsAdmin || Zichtbaar.MagEen(MenuKeys.AppInstellingen, MenuKeys.SportlinkExtensie, MenuKeys.BegeleidingImport,
            MenuKeys.EmailTemplates, MenuKeys.Teamaliassen, MenuKeys.Thema);

    protected override void OnInitialized()
    {
        OpenGroepVoorRoute(Navigation.Uri);
        Navigation.LocationChanged += OnLocationChanged;
        ThemeService.OnThemeChanged += OnThemeChanged;
        ClubSelector.OnChange += OnClubChanged;
        ClubSelector.OnClubNameChange += OnMenuWeergaveChanged;
        ClubSelector.OnSportlinkExtensionChange += OnMenuWeergaveChanged;
        // Club-naam via ClubSelector.SelectedClubName (gevuld door MainLayout) — geen eigen API-call
    }

    protected override Task OnInitializedAsync() => MenuStand.LaadAsync();

    public void Dispose()
    {
        Navigation.LocationChanged -= OnLocationChanged;
        ThemeService.OnThemeChanged -= OnThemeChanged;
        ClubSelector.OnChange -= OnClubChanged;
        ClubSelector.OnClubNameChange -= OnMenuWeergaveChanged;
        ClubSelector.OnSportlinkExtensionChange -= OnMenuWeergaveChanged;
    }

    // #1578: naam- en vlagwijzigingen hertekenen alleen het menu; pagina's herladen er niet op.
    private void OnMenuWeergaveChanged() => _ = InvokeAsync(StateHasChanged);
    private void OnThemeChanged() => _ = InvokeAsync(StateHasChanged);
    private void OnClubChanged() => _ = InvokeAsync(async () => { await MenuStand.LaadAsync(); StateHasChanged(); });

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        OpenGroepVoorRoute(e.Location);
        _ = InvokeAsync(StateHasChanged);
    }

    private void OpenGroepVoorRoute(string uri)
    {
        var pad = Navigation.ToBaseRelativePath(uri);
        var einde = pad.IndexOfAny(['?', '#']);
        if (einde >= 0) pad = pad[..einde];
        pad = pad.Trim('/');
        foreach (var (groep, routes) in GroepRoutes)
            if (routes.Contains(pad, StringComparer.OrdinalIgnoreCase))
                _open.Add(groep);
    }

    private bool IsOpen(string groep) => _open.Contains(groep);

    private void ToggleGroep(string groep)
    {
        if (!_open.Remove(groep)) _open.Add(groep);
    }

    private void ToggleNavMenu() => collapseNavMenu = !collapseNavMenu;

    private async Task LogoutAsync() => await Auth.LogoutAsync();
}
