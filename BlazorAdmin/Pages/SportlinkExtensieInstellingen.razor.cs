using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>Code-behind van <c>SportlinkExtensieInstellingen.razor</c> (#988/#991/#998/#1113, code-behind sinds #1122).</summary>
public partial class SportlinkExtensieInstellingen : ClubSelectorPageBase, IDisposable
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private AppSettingsDto? settings;
    private bool saving;
    private string? successMessage;
    private string? errorMessage;

    private List<SportlinkExtensieRolDto> sportlinkRollen = new();
    private SportlinkExtensieRolDto? registreerRol;
    private string? registreerSportlinkAccountNaam;
    private string? sportlinkKoppelMessage;
    private string? registreerRefreshToken;
    private string? sportlinkTokenMessage;

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

    private bool _isTestmodus => ClubSelector.SelectedClubCode == "ALLSTARS";

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override Task OnClubChangedAsync()
    {
        SluitAutoLoginFormulier();
        autoLoginStatus = null;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        errorMessage = null;
        settings = null;
        if (_isTestmodus) return;

        var r = await Api.GetSettingsAsync();
        if (r.Success) settings = r.Data;
        else errorMessage = r.ErrorMessage;

        await LaadSportlinkExtensieRollenAsync();
        await LaadAutoLoginStatusAsync();
        await LaadSportlinkHealthAsync();
    }

    private async Task LaadAutoLoginStatusAsync()
    {
        autoLoginError = null;
        var r = await Api.GetSportlinkAutoLoginStatusAsync(AutoLoginRolNaam);
        autoLoginStatus = r.Success ? r.Data : null;
        if (!r.Success) autoLoginError = "Status van automatisch inloggen kon niet worden opgehaald.";
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

    public void Dispose() => WisAutoLoginGeheimen();

    private async Task BewaarAutoLoginAsync()
    {
        if (autoLoginBusy || string.IsNullOrWhiteSpace(autoLoginForm.Username) ||
            string.IsNullOrWhiteSpace(autoLoginForm.Password) || string.IsNullOrWhiteSpace(autoLoginForm.TotpSecret))
            return;

        autoLoginBusy = true;
        autoLoginError = null;
        autoLoginMessage = null;
        try
        {
            var r = await Api.SetSportlinkAutoLoginAsync(AutoLoginRolNaam, autoLoginForm);
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
            var r = await Api.DeleteSportlinkAutoLoginAsync(AutoLoginRolNaam);
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
        var r = await Api.GetSportlinkExtensieRollenAsync();
        sportlinkRollen = r.Success ? r.Data ?? new() : new();
    }

    private async Task LaadSportlinkHealthAsync(bool live = false)
    {
        sportlinkHealthLoading = true;
        sportlinkHealthError = null;
        StateHasChanged();
        try
        {
            var r = await Api.GetSportlinkExtensieHealthAsync(live);
            if (r.Success) sportlinkHealth = r.Data;
            else sportlinkHealthError = r.ErrorMessage ?? "Onbekende fout bij ophalen status.";
        }
        finally
        {
            sportlinkHealthLoading = false;
        }
    }

    // Alleen deze twee velden — een bewust kleine, aparte "Velden"-update (partial update, raakt
    // geen andere instellingen op de hoofd-Instellingen-pagina).
    private async Task OpslaanAsync()
    {
        if (settings == null) return;
        saving = true;
        successMessage = null;
        errorMessage = null;

        var update = new SettingsUpdateDto
        {
            Velden = new()
            {
                ["SportlinkExtensionEnabled"] = settings.SportlinkExtensionEnabled ? "1" : "0",
                ["SportlinkDryRun"] = settings.SportlinkDryRun ? "1" : "0",
            }
        };

        var r = await Api.UpdateSettingsAsync(update);
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
        registreerRefreshToken = null;
        sportlinkTokenMessage = null;
    }

    // #991: schrijft het échte refresh-token weg — write-only, nooit teruggetoond.
    private async Task BevestigRegistreerTokenAsync()
    {
        if (registreerRol == null || string.IsNullOrWhiteSpace(registreerRefreshToken)) return;
        var r = await Api.RegistreerSportlinkTokenAsync(registreerRol.RolNaam, registreerRefreshToken);
        registreerRefreshToken = null;
        sportlinkTokenMessage = r.Success
            ? "Token geregistreerd en gevalideerd."
            : "Fout: " + r.ErrorMessage;
    }

    private async Task BevestigRegistreerKoppelingAsync()
    {
        if (registreerRol == null) return;
        var r = await Api.RegistreerSportlinkKoppelingAsync(registreerRol.RolNaam, registreerSportlinkAccountNaam);
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
