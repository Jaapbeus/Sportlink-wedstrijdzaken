using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>
/// Code-behind van <c>Thema.razor</c> (#1270): de twee kleurpaletten (licht en donker), de
/// basisthema's en het uitlezen van kleuren uit de clubwebsite.
/// </summary>
/// <remarks>
/// Verhuisd uit het <c>@code</c>-blok van de pagina conform regel 3 van
/// <c>docs/ARCHITECTUUR-CODEKWALITEIT.md</c>: een <c>@code</c>-blok is niet los te testen, een
/// partial class wel. De logica zelf is ongewijzigd overgenomen — dit is een verplaatsing, geen
/// herontwerp.
/// </remarks>
public partial class Thema : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;
    [Inject] private ThemeService ThemeService { get; set; } = default!;

    private ThemeDto _theme = new();
    private bool _loading = true;
    private bool _saving = false;
    private bool _extracting = false;
    private string? _error;
    private string? _saveMessage;
    private bool _saveSuccess;
    private string? _extractError;
    private List<string> _extractedColors = new();

    // De twee paletten die bewerkt worden. _modus bepaalt welke de pickers tonen én welke weergave
    // de hele interface laat zien, zodat de beheerder meteen ziet wat hij instelt.
    private Dictionary<string, string> _licht = new();
    private Dictionary<string, string> _donker = new();
    private string _modus = "light";
    private string? _extractedFaviconUrl;
    private string? _extractedLogoUrl;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override Task OnClubChangedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        var result = await Api.GetThemeAsync();
        if (result.Success && result.Data != null)
        {
            _theme = result.Data;
            VulPalettenUitThema();
        }
        else
        {
            _error = result.ErrorMessage ?? "Thema kon niet worden geladen.";
        }
        _modus = await ThemeService.GetModeAsync();
        _loading = false;
    }

    /// <summary>
    /// Zet de twee bewerkbare paletten klaar. Heeft de club nog geen eigen set (#1254), dan begint
    /// het lichte palet bij de vier platte kleuren aangevuld met de standaardwaarden — zo ziet de
    /// beheerder zijn huidige thema terug in plaats van een leeg formulier.
    /// </summary>
    private void VulPalettenUitThema()
    {
        _licht = new Dictionary<string, string>(ThemePresets.StandaardLicht);
        if (_theme.LightColors is { Count: > 0 })
        {
            foreach (var (sleutel, waarde) in _theme.LightColors) _licht[sleutel] = waarde;
        }
        else
        {
            _licht["primary"]       = _theme.Primary;
            _licht["secondary"]     = _theme.Secondary;
            _licht["accent"]        = _theme.Accent;
            _licht["textOnPrimary"] = _theme.TextOnPrimary;
        }

        _donker = new Dictionary<string, string>(ThemePresets.StandaardDonker);
        if (_theme.DarkColors is { Count: > 0 })
        {
            foreach (var (sleutel, waarde) in _theme.DarkColors) _donker[sleutel] = waarde;
        }
    }

    private Dictionary<string, string> ActiefPalet => _modus == "dark" ? _donker : _licht;

    private string Kleur(string sleutel) => ActiefPalet.TryGetValue(sleutel, out var waarde) ? waarde : "#000000";

    private async Task ModusBewerkenAsync(string modus)
    {
        _modus = modus;
        // De hele interface schakelt mee: anders stelt de beheerder donkere kleuren in terwijl hij
        // naar een licht scherm kijkt.
        await ThemeService.SetModeAsync(modus);
        await ToepassenAsync();
    }

    private async Task PresetToepassen(ChangeEventArgs e)
    {
        var naam = e.Value?.ToString();
        if (string.IsNullOrWhiteSpace(naam)) return;

        var preset = ThemePresets.Alle.FirstOrDefault(p => p.Naam == naam);
        if (preset == null) return;

        var bron = _modus == "dark" ? preset.Donker : preset.Licht;
        var doel = ActiefPalet;
        foreach (var (sleutel, waarde) in bron) doel[sleutel] = waarde;

        await ToepassenAsync();
    }

    /// <summary>Past beide paletten toe op de live interface, zonder op te slaan.</summary>
    private async Task ToepassenAsync()
    {
        SynchroniseerNaarThema();
        await ThemeService.ApplyAsync(_theme);
        StateHasChanged();
    }

    /// <summary>
    /// Schrijft de paletten terug naar de DTO. De vier platte velden blijven meelopen met het
    /// lichte palet: die zijn de terugval voor een client die de paletten nog niet kent (#1254),
    /// en zouden anders stilzwijgend uit de pas gaan lopen met wat de beheerder ziet.
    /// </summary>
    private void SynchroniseerNaarThema()
    {
        _theme.LightColors = new Dictionary<string, string>(_licht);
        _theme.DarkColors  = new Dictionary<string, string>(_donker);
        _theme.Primary       = _licht["primary"];
        _theme.Secondary     = _licht["secondary"];
        _theme.Accent        = _licht["accent"];
        _theme.TextOnPrimary = _licht["textOnPrimary"];
    }

    private void OnColorChange(string sleutel, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        // Een kale hexcode zonder # aanvullen, zes cijfers of acht (de laatste twee zijn alpha).
        if ((value.Length == 6 || value.Length == 8) &&
            System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-fA-F]+$"))
            value = "#" + value;

        ActiefPalet[sleutel] = value;
        _ = ToepassenAsync();
    }

    private async Task SaveAsync()
    {
        _saving = true;
        _saveMessage = null;
        StateHasChanged();

        SynchroniseerNaarThema();
        var result = await Api.UpdateThemeAsync(_theme);
        _saveSuccess = result.Success;
        _saveMessage = result.Success ? "Thema opgeslagen." : (result.ErrorMessage ?? "Opslaan mislukt.");
        _saving = false;
    }

    private async Task ResetToDefaultAsync()
    {
        _theme = new ThemeDto { FaviconUrl = null, LogoUrl = null };
        _licht = new Dictionary<string, string>(ThemePresets.StandaardLicht);
        _donker = new Dictionary<string, string>(ThemePresets.StandaardDonker);
        await ToepassenAsync();
    }

    private async Task ExtractFromWebsiteAsync()
    {
        _extracting = true;
        _extractError = null;
        _extractedColors = new();
        _extractedFaviconUrl = null;
        _extractedLogoUrl = null;
        StateHasChanged();

        var result = await Api.ExtractThemeColorsAsync(_theme.ClubWebsiteUrl ?? "");
        if (result.Success && result.Data != null)
        {
            _extractedColors = result.Data.Colors;
            _extractedFaviconUrl = result.Data.FaviconUrl;
            _extractedLogoUrl = result.Data.LogoUrl;
        }
        else
        {
            _extractError = result.ErrorMessage ?? "Ophalen mislukt.";
        }

        _extracting = false;
    }
}
