using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>
/// Code-behind van <c>RolFeatureInstellingen.razor</c> (#1390, opvolger van #1341/epic #1338) —
/// de toegangsmatrix (Optie A): rijen zijn elk menu-item/functie, kolommen zijn de vier instelbare
/// rollen. Bewust geen kolom voor <c>admin</c>: admin heeft altijd alles aan en dat voegt niets toe
/// aan het overzicht. De kolommen worden vanuit <see cref="Rollen"/> gerenderd (geen hardcoded
/// markup per rol) zodat een 5e/6e rol later een databasegelijke toevoeging is; bij een sterke
/// groei in rolaantal (niet op korte termijn verwacht) is overstappen naar een rol-voor-rol-lijst
/// dan een nieuwe pagina op dezelfde API, geen nieuw datamodel.
/// </summary>
/// <remarks>
/// BlazorAdmin (WASM-client) heeft geen projectreferentie naar <c>Planner.Shared</c> — dezelfde
/// reden waarom <c>AdminModels.cs</c> al zijn eigen DTO's heeft in plaats van de servertypes te
/// hergebruiken. De FeatureKey/RolNaam-strings hieronder zijn dus letterlijke kopieën van
/// <c>Planner.Shared.Autorisatie.RolNamen</c>/<c>MenuFeatureKeys</c> en
/// <c>Planner.Shared.Integrations.SportlinkClub.SportlinkRolFeature</c> — bij een wijziging aan
/// die servertypes moeten deze meegroeien.
/// </remarks>
public partial class RolFeatureInstellingen : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    /// <summary>Eén rij van de matrix — puur UI-metadata, geen logica.</summary>
    private sealed record MatrixRij(string Groep, string FeatureKey, string Label, string? Beschrijving, bool Avg);

    private static readonly IReadOnlyList<MatrixRij> Rijen = new[]
    {
        new MatrixRij("Algemeen", "menu.dashboard", "Dashboard", null, false),
        new MatrixRij("Algemeen", "menu.teambegeleiding", "Teambegeleiding",
            "Namen, contactgegevens en rollen per team.", true),
        new MatrixRij("Algemeen", "menu.planning", "Planning", null, false),
        new MatrixRij("Algemeen", "menu.veldoptimalisatie", "Veld optimalisatie", null, false),
        new MatrixRij("Algemeen", "menu.leermomenten", "Leermomenten", null, false),
        new MatrixRij("Algemeen", "menu.teamaliassen", "Teamaliassen", null, false),
        new MatrixRij("Algemeen", "menu.emailtester", "Email-tester", null, false),

        new MatrixRij("Sportlink extensie", "menu.wijzigingsverzoeken", "Wijzigingsverzoeken", null, false),
        new MatrixRij("Sportlink extensie", "menu.wedstrijdaanmaken", "Wedstrijd aanmaken", null, false),
        new MatrixRij("Sportlink extensie", "sportlink.kleedkamers", "Kleedkamers toewijzen",
            "Kleedkamer-nummers invoeren en opslaan bij een wedstrijd.", false),
        new MatrixRij("Sportlink extensie", "sportlink.scheidsrechter", "Scheidsrechters toewijzen",
            "Relatiecode van scheidsrechter/AR1/AR2 invoeren en opslaan.", false),
        new MatrixRij("Sportlink extensie", "sportlink.veld", "Veld wijzigen",
            "FieldId/FieldSize van een wedstrijd wijzigen.", false),

        new MatrixRij("Instellingen", "menu.instellingen.speeltijden", "Speeltijden", null, false),
        new MatrixRij("Instellingen", "menu.instellingen.velden", "Velden", null, false),
        new MatrixRij("Instellingen", "menu.instellingen.begeleidingimport", "Begeleiding importeren",
            "Volledige CSV met persoonsgegevens van begeleiders.", true),
        new MatrixRij("Instellingen", "menu.instellingen.voorkeurstijden", "Voorkeurstijden", null, false),
        new MatrixRij("Instellingen", "menu.instellingen.emailtemplates", "E-mailtemplates", null, false),
        new MatrixRij("Instellingen", "menu.instellingen.thema", "Thema", null, false),
        new MatrixRij("Instellingen", "menu.instellingen.sportlinkextensie", "Sportlink-instellingen", null, false),
    };

    /// <summary>Rolkolommen in weergavevolgorde — bewust zonder <c>admin</c> (zie klassecomment).</summary>
    private static readonly IReadOnlyList<string> Rollen = new[] { "user", "Wedstrijdzaken", "Sectiehoofd", "Ledenadministratie" };

    private static readonly IReadOnlyDictionary<string, string> RolLabels = new Dictionary<string, string>
    {
        ["user"] = "Gebruiker (standaard)",
        ["Wedstrijdzaken"] = "Wedstrijdzaken",
        ["Sectiehoofd"] = "Sectiehoofd",
        ["Ledenadministratie"] = "Ledenadministratie",
    };

    private IEnumerable<IGrouping<string, MatrixRij>> Groepen => Rijen.GroupBy(r => r.Groep);

    private Dictionary<(string RolNaam, string FeatureKey), bool> _instellingen = new();
    private string? _errorMessage;
    private string? _successMessage;
    private bool _laden = true;

    protected override async Task OnInitializedAsync() => await LaadAsync();

    protected override Task OnClubChangedAsync() => LaadAsync();

    private async Task LaadAsync()
    {
        _laden = true;
        _errorMessage = null;
        _successMessage = null;
        StateHasChanged();

        var r = await Api.GetRolFeatureInstellingenAsync();
        _instellingen = r.Success
            ? (r.Data ?? new()).ToDictionary(i => (i.RolNaam, i.FeatureKey), i => i.Enabled)
            : new();
        if (!r.Success) _errorMessage = r.ErrorMessage;
        _laden = false;
    }

    private bool IsAan(string rolNaam, string featureKey) =>
        _instellingen.TryGetValue((rolNaam, featureKey), out var aan) && aan;

    private async Task ZetAsync(string rolNaam, string featureKey, bool enabled)
    {
        _successMessage = null;
        _errorMessage = null;

        var r = await Api.ZetRolFeatureInstellingAsync(rolNaam, featureKey, enabled);
        if (r.Success)
        {
            _instellingen[(rolNaam, featureKey)] = enabled;
            _successMessage = "Instelling opgeslagen.";
        }
        else
        {
            _errorMessage = r.ErrorMessage;
        }
    }
}
