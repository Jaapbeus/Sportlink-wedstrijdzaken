using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class Instellingen : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private AppSettingsDto? settings;
    private bool saving;
    private string? errorMessage;
    private string? successMessage;
    private string? herstartMessage;

    private bool geocoding;
    private string? geocodeMessage;
    private bool geocodeError;

    private List<UitgeslotenEmailAdresDto> uitgeslotenEmails = new();
    private UitgeslotenEmailAdresDto? nieuwAdres;
    private string? uitgeslotenMessage;

    // Sync + email log (verplaatst van Dashboard #344)
    private SyncStatusDto? _status;
    private EmailLogResponse? _emailLog;
    private bool _syncLoading = true;
    private bool _syncing;
    private string? _syncResult;
    private int _emailVerwerkt, _emailFouten, _emailBuitenScope, _emailGeenAntwoord;

    private bool _isTestmodus => ClubSelector.SelectedClubCode == "ALLSTARS";

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override Task OnClubChangedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        errorMessage = null;
        settings = null;
        _syncLoading = true;

        if (_isTestmodus)
        {
            _status = null;
            _emailLog = null;
            _syncLoading = false;
            return;
        }

        var settingsTask = Api.GetSettingsAsync();
        var syncTask    = Api.GetSyncStatusAsync();
        var emailTask   = Api.GetEmailLogAsync(vanaf: DateTime.Today.AddDays(-1), limit: 200);

        await Task.WhenAll(settingsTask, syncTask, emailTask);

        var r = await settingsTask;
        if (r.Success) settings = r.Data;
        else errorMessage = r.ErrorMessage;

        var syncResult = await syncTask;
        if (syncResult.Success) _status = syncResult.Data;

        var emailResult = await emailTask;
        if (emailResult.Success)
        {
            _emailLog = emailResult.Data;
            if (_emailLog?.Items != null)
            {
                _emailVerwerkt    = _emailLog.Items.Count(i => i.Status == "AntwoordVerstuurd");
                // #572: verwerkt zonder automatisch antwoord — planning was mogelijk
                _emailGeenAntwoord = _emailLog.Items.Count(i => i.Status == "GeenAntwoordNodig");
                _emailFouten      = _emailLog.Items.Count(i => i.Status == "Fout");
                _emailBuitenScope = _emailLog.Items.Count(i => i.Status == "BuitenScope");
            }
        }

        _syncLoading = false;
        await LaadUitgeslotenEmailsAsync();
    }

    private async Task TriggerSyncAsync()
    {
        _syncing = true;
        _syncResult = null;
        var r = await Api.TriggerSyncAsync();
        if (!r.Success || r.Data?.JobId is not Guid jobId)
        {
            _syncResult = $"Sync starten mislukt: {r.ErrorMessage}";
            _syncing = false;
            return;
        }
        _syncResult = "Synchronisatie gestart, even geduld…";
        StateHasChanged();
        // #1138: pollt de job-status i.p.v. te gissen naar een wijziging in LastSyncTimestamp —
        // zo is ook een mislukte job direct zichtbaar in plaats van na 10 minuten stilzwijgend te stoppen.
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000);
            var statusResult = await Api.GetSyncStatusAsync(jobId);
            var job = statusResult.Success ? statusResult.Data?.Job : null;
            if (job?.Status == "succeeded")
            {
                _status = statusResult!.Data;
                _syncResult = "Synchronisatie voltooid.";
                _syncing = false;
                StateHasChanged();
                return;
            }
            if (job?.Status == "failed")
            {
                _status = statusResult!.Data;
                _syncResult = $"Synchronisatie mislukt: {job.ErrorMessage}";
                _syncing = false;
                StateHasChanged();
                return;
            }
        }
        _syncResult = "Synchronisatie loopt op de achtergrond. Herlaad de pagina later voor de actuele status.";
        _syncing = false;
        await LoadAsync();
    }

    private static string CronNaarNederlands(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return "Onbekend schema";
        var p = cron.Trim().Split(' ');
        if (p.Length != 6) return cron;
        var (sec, min, hour, day, month, weekday) = (p[0], p[1], p[2], p[3], p[4], p[5]);
        if (sec == "0" && day == "*" && month == "*" && weekday == "*")
        {
            if (int.TryParse(hour, out var h) && int.TryParse(min, out var m))
                return m == 0 ? $"Elke dag om {h:D2}:00" : $"Elke dag om {h:D2}:{m:D2}";
        }
        if (sec == "0" && min == "0" && hour == "*" && day == "*" && month == "*" && weekday == "*")
            return "Elk uur";
        string[] dagNamen = { "", "maandag", "dinsdag", "woensdag", "donderdag", "vrijdag", "zaterdag", "zondag" };
        if (sec == "0" && min == "0" && day == "*" && month == "*"
            && int.TryParse(hour, out var h2) && int.TryParse(weekday, out var wd) && wd >= 0 && wd <= 7)
        {
            var dag = wd == 0 ? dagNamen[7] : wd < dagNamen.Length ? dagNamen[wd] : $"dag {wd}";
            return $"Elke {dag} om {h2:D2}:00";
        }
        return cron;
    }

    private async Task LaadUitgeslotenEmailsAsync()
    {
        var r = await Api.GetUitgeslotenEmailsAsync();
        uitgeslotenEmails = r.Success ? r.Data ?? new() : new();
    }

    private void StartNieuwAdres()
    {
        nieuwAdres = new UitgeslotenEmailAdresDto { Actief = true };
        uitgeslotenMessage = null;
    }

    private async Task OpslaanNieuwAdresAsync()
    {
        if (nieuwAdres == null || string.IsNullOrWhiteSpace(nieuwAdres.EmailAdres))
        {
            uitgeslotenMessage = "E-mailadres is verplicht.";
            return;
        }
        var r = await Api.CreateUitgeslotenEmailAsync(nieuwAdres);
        if (r.Success)
        {
            nieuwAdres = null;
            uitgeslotenMessage = null;
            await LaadUitgeslotenEmailsAsync();
        }
        else
        {
            uitgeslotenMessage = "Fout: " + r.ErrorMessage;
        }
    }

    private async Task VerwijderAdresAsync(int id)
    {
        var r = await Api.DeleteUitgeslotenEmailAsync(id);
        uitgeslotenMessage = r.Success ? null : "Fout: " + r.ErrorMessage;
        await LaadUitgeslotenEmailsAsync();
    }

    private async Task ZoekCoordinaten()
    {
        if (settings == null || string.IsNullOrWhiteSpace(settings.AccommodatiePlaats)) return;
        geocoding = true;
        geocodeMessage = null;
        geocodeError = false;
        var r = await Api.GeocodeAsync(settings.AccommodatiePlaats);
        if (r.Success && r.Data != null)
        {
            settings.AccommodatieLatitude = r.Data.Lat;
            settings.AccommodatieLongitude = r.Data.Lon;
            geocodeMessage = $"Gevonden: {r.Data.DisplayName}";
        }
        else
        {
            geocodeError = true;
            geocodeMessage = r.ErrorMessage ?? "Niet gevonden";
        }
        geocoding = false;
    }

    private async Task OpslaanAsync()
    {
        if (settings == null) return;
        saving = true;
        successMessage = null;
        herstartMessage = null;
        errorMessage = null;

        var update = new SettingsUpdateDto
        {
            Velden = new()
            {
                ["Accommodatie"] = settings.Accommodatie,
                ["AccommodatiePlaats"] = settings.AccommodatiePlaats,
                ["AccommodatieLatitude"] = settings.AccommodatieLatitude?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["AccommodatieLongitude"] = settings.AccommodatieLongitude?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["HerplanDeadlineDagen"] = settings.HerplanDeadlineDagen?.ToString(),
                ["BufferMinuten"] = settings.BufferMinuten?.ToString(),
                ["PlannerAfzenderNaam"] = settings.PlannerAfzenderNaam,
                ["PlannerEmailAdres"] = settings.PlannerEmailAdres,
                ["CoordinatorNaam"] = settings.CoordinatorNaam,
                ["CoordinatorFunctie"] = settings.CoordinatorFunctie,
                ["FetchSchedule"] = settings.FetchSchedule,
                ["UseRealtimeApi"] = settings.UseRealtimeApi ? "1" : "0",
                ["KnvbPdfBijlageIngeschakeld"] = settings.KnvbPdfBijlageIngeschakeld ? "1" : "0",
                ["KnvbStandaardRegio"] = settings.KnvbStandaardRegio,
            }
        };

        var r = await Api.UpdateSettingsAsync(update);
        if (r.Success)
        {
            successMessage = "Instellingen opgeslagen.";
            if (r.Data?.HerstartAutomatisch == true)
            {
                herstartMessage = r.Data.Opmerking ?? "Het ophaalschema is bijgewerkt. De applicatie herstart automatisch en het nieuwe schema is actief na de herstart.";
                // CRON-preview bijwerken in het scherm
                if (r.Data.FetchScheduleLeesbaar != null)
                    settings.FetchScheduleLeesbaar = r.Data.FetchScheduleLeesbaar;
                if (r.Data.VolgendeMomenten != null)
                    settings.VolgendeMomenten = r.Data.VolgendeMomenten;
            }
            else if (r.Data?.HerstartVereist == true)
            {
                herstartMessage = r.Data.Opmerking ?? "Het ophaalschema is opgeslagen. Herstart de Function App handmatig om het nieuwe schema actief te maken.";
            }
        }
        else
        {
            errorMessage = r.ErrorMessage;
        }
        saving = false;
    }
}
