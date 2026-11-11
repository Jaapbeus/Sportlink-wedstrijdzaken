using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>Code-behind van <c>SportlinkExtensieInstellingen.razor</c> (#988/#991/#998/#1113, code-behind sinds #1122).</summary>
public partial class SportlinkExtensieInstellingen : ClubSelectorPageBase
{
    [Inject] private ClubInstellingenCache InstellingenCache { get; set; } = default!;
    [Inject] private SportlinkApiClient SportlinkApi { get; set; } = default!;

    private AppSettingsDto? settings;
    private bool saving;
    private string? successMessage;
    private string? errorMessage;

    private List<SportlinkExtensieRolDto> sportlinkRollen = new();
    private SportlinkExtensieRolDto? registreerRol;
    private string? registreerSportlinkAccountNaam;
    private string? sportlinkKoppelMessage;

    private const string AutoLoginRolNaam = "Wedstrijdzaken";
    private SportlinkAutoLoginStatusDto? autoLoginStatus;
    private SportlinkAutoLoginRequestDto autoLoginForm = new();
    private bool autoLoginFormOpen;
    private bool autoLoginBusy;
    private string? autoLoginMessage;
    private string? autoLoginError;

    private SportlinkExtensieHealthDto? sportlinkHealth;
    private bool sportlinkHealthLoading;
    private string? sportlinkHealthError;

    // #1872: elke clubgebonden lookup heeft een eigen guard, zodat een late response van de vorige club
    // nooit de gegevens (of een latere save/koppeling) van de huidige club overschrijft.
    private readonly LookupGeneratieGuard _instellingenGuard = new();
    private readonly LookupGeneratieGuard _rollenGuard = new();
    private readonly LookupGeneratieGuard _autoLoginGuard = new();
    private readonly LookupGeneratieGuard _healthGuard = new();
    private string? _settingsClubCode;

    private bool _isTestmodus => ClubSelector.SelectedClubCode == "ALLSTARS";

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override Task OnClubChangedAsync()
    {
        SluitAutoLoginFormulier();
        autoLoginStatus = null;
        // #1872: een open koppeling of melding hoort bij de vorige club.
        registreerRol = null;
        sportlinkKoppelMessage = null;
        successMessage = null;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        errorMessage = null;
        settings = null;
        if (_isTestmodus) return;

        // #1578: vier onafhankelijke aanroepen (elk vult eigen velden) parallel in plaats van na elkaar.
        await Task.WhenAll(
            LaadInstellingenAsync(),
            LaadSportlinkExtensieRollenAsync(),
            LaadAutoLoginStatusAsync(),
            LaadSportlinkHealthAsync());
    }

    private async Task LaadInstellingenAsync()
    {
        await LaadClubgebondenAsync(_instellingenGuard, () => InstellingenCache.GetSettingsAsync(), r =>
        {
            if (r.Success)
            {
                settings = r.Data;
                _settingsClubCode = ClubSelector.SelectedClubCode;
            }
            else errorMessage = r.ErrorMessage;
        });
    }

    private async Task LaadAutoLoginStatusAsync()
    {
        autoLoginError = null;
        await LaadClubgebondenAsync(_autoLoginGuard, () => SportlinkApi.GetSportlinkAutoLoginStatusAsync(AutoLoginRolNaam), r =>
        {
            autoLoginStatus = r.Success ? r.Data : null;
            if (!r.Success) autoLoginError = "Status van automatisch inloggen kon niet worden opgehaald.";
        });
    }

    private void StartAutoLoginInstellen()
    {
        WisAutoLoginGeheimen();
        autoLoginForm = new();
        autoLoginFormOpen = true;
        autoLoginMessage = null;
        autoLoginError = null;
    }

    private void SluitAutoLoginFormulier()
    {
        WisAutoLoginGeheimen();
        autoLoginForm = new();
        autoLoginFormOpen = false;
        autoLoginMessage = null;
    }

    private void WisAutoLoginGeheimen()
    {
        autoLoginForm.Username = "";
        autoLoginForm.Password = "";
        autoLoginForm.TotpSecret = "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WisAutoLoginGeheimen();
        base.Dispose(disposing);
    }

    private async Task BewaarAutoLoginAsync()
    {
        if (autoLoginBusy || string.IsNullOrWhiteSpace(autoLoginForm.Username) ||
            string.IsNullOrWhiteSpace(autoLoginForm.Password) || string.IsNullOrWhiteSpace(autoLoginForm.TotpSecret))
            return;

        autoLoginBusy = true;
        autoLoginError = null;
        autoLoginMessage = null;
        var clubCode = ClubSelector.SelectedClubCode;
        try
        {
            var r = await SportlinkApi.SetSportlinkAutoLoginAsync(AutoLoginRolNaam, autoLoginForm);
            if (IsClubGewisseld(clubCode)) return; // #1872: uitkomst hoort bij een andere club
            if (r.Success)
            {
                autoLoginStatus = r.Data;
                autoLoginMessage = "Automatisch inloggen is ingesteld. De gegevens worden bij de volgende automatische vernieuwing gebruikt.";
                autoLoginFormOpen = false;
            }
            else
            {
                autoLoginError = "Instellen is mislukt. Controleer de invoer en probeer opnieuw.";
            }
        }
        catch
        {
            autoLoginError = "Instellen is mislukt door een onverwachte fout.";
        }
        finally
        {
            WisAutoLoginGeheimen();
            autoLoginBusy = false;
        }
    }

    private async Task VerwijderAutoLoginAsync()
    {
        if (autoLoginBusy) return;
        autoLoginBusy = true;
        autoLoginError = null;
        autoLoginMessage = null;
        try
        {
            var clubCode = ClubSelector.SelectedClubCode;
            var r = await SportlinkApi.DeleteSportlinkAutoLoginAsync(AutoLoginRolNaam);
            if (IsClubGewisseld(clubCode)) return; // #1872
            if (r.Success)
            {
                autoLoginStatus = r.Data;
                autoLoginMessage = "Opgeslagen inloggegevens verwijderd.";
                SluitAutoLoginFormulier();
            }
            else autoLoginError = "Verwijderen is mislukt. Probeer het later opnieuw.";
        }
        catch
        {
            autoLoginError = "Verwijderen is mislukt door een onverwachte fout.";
        }
        finally
        {
            WisAutoLoginGeheimen();
            autoLoginBusy = false;
        }
    }

    private async Task LaadSportlinkExtensieRollenAsync()
    {
        await LaadClubgebondenAsync(_rollenGuard, () => SportlinkApi.GetSportlinkExtensieRollenAsync(),
            r => sportlinkRollen = r.Success ? r.Data ?? new() : new());
    }

    private async Task LaadSportlinkHealthAsync(bool live = false)
    {
        sportlinkHealthLoading = true;
        sportlinkHealthError = null;
        StateHasChanged();
        var toegepast = false;
        try
        {
            toegepast = await LaadClubgebondenAsync(_healthGuard, () => SportlinkApi.GetSportlinkExtensieHealthAsync(live), r =>
            {
                if (r.Success) sportlinkHealth = r.Data;
                else sportlinkHealthError = r.ErrorMessage ?? "Onbekende fout bij ophalen status.";
            });
        }
        finally
        {
            // Bij een verouderde load beheert de nieuwere load de laadstatus.
            if (toegepast) sportlinkHealthLoading = false;
        }
    }

    // Alleen deze drie velden — een bewust kleine, aparte "Velden"-update (partial update, raakt
    // geen andere instellingen op de hoofd-Instellingen-pagina).
    private async Task OpslaanAsync()
    {
        // #1872: nooit de instellingen van een andere club naar de huidige club schrijven.
        if (settings == null || IsClubGewisseld(_settingsClubCode)) return;
        var clubCode = ClubSelector.SelectedClubCode;
        saving = true;
        successMessage = null;
        errorMessage = null;

        var update = new SettingsUpdateDto
        {
            Velden = new()
            {
                ["SportlinkExtensionEnabled"] = settings.SportlinkExtensionEnabled ? "1" : "0",
                ["SportlinkDryRun"] = settings.SportlinkDryRun ? "1" : "0",
                ["SportlinkSpelactiviteit"] = string.IsNullOrWhiteSpace(settings.SportlinkSpelactiviteit) ? null : settings.SportlinkSpelactiviteit.Trim(),
            }
        };

        var r = await InstellingenCache.UpdateSettingsAsync(update);
        if (IsClubGewisseld(clubCode))
        {
            saving = false;
            return;
        }
        if (r.Success)
        {
            successMessage = "Instellingen opgeslagen.";
            // #1122: menu-items Wijzigingsverzoeken/Oefenwedstrijd volgen de schakelaar direct.
            ClubSelector.ZetSportlinkExtensionEnabled(settings.SportlinkExtensionEnabled);
        }
        else errorMessage = r.ErrorMessage;
        saving = false;
    }

    private void StartRegistreerKoppeling(SportlinkExtensieRolDto rol)
    {
        registreerRol = rol;
        registreerSportlinkAccountNaam = rol.SportlinkAccountNaam;
        sportlinkKoppelMessage = null;
    }

    private async Task BevestigRegistreerKoppelingAsync()
    {
        if (registreerRol == null) return;
        var clubCode = ClubSelector.SelectedClubCode;
        var r = await SportlinkApi.RegistreerSportlinkKoppelingAsync(registreerRol.RolNaam, registreerSportlinkAccountNaam);
        if (IsClubGewisseld(clubCode)) return; // #1872: uitkomst hoort bij een andere club
        if (r.Success)
        {
            registreerRol = null;
            sportlinkKoppelMessage = null;
            await LaadSportlinkExtensieRollenAsync();
        }
        else
        {
            sportlinkKoppelMessage = "Fout: " + r.ErrorMessage;
        }
    }
}
