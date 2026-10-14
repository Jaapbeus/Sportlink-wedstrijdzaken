using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Planner.Shared.Scheduling;
using Planner.Shared.Sync;

namespace BlazorAdmin.Pages;

public partial class Instellingen : ClubSelectorPageBase
{
    [Inject] private EmailApiClient EmailApi { get; set; } = default!;
    [Inject] private ClubInstellingenCache InstellingenCache { get; set; } = default!;
    [Inject] private InstellingenApiClient InstellingenApi { get; set; } = default!;

    private AppSettingsDto? settings;

    // #1715: de opgeslagen startmaand. Alleen een gewijzigde waarde gaat mee in de PUT, zodat opslaan zonder
    // wijziging het seizoen niet opnieuw opbouwt en geen extra auditregel schrijft.
    private int _opgeslagenStartMaand = SeizoenStartMaand.Standaard;
    private bool StartMaandGewijzigd => settings != null && SeizoenStartMaand.OfStandaard(settings.SeasonStartMonth) != _opgeslagenStartMaand;
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
    private bool _toonResetBevestiging;
    private int _resetSeizoen = HuidigSeizoenStartjaar();
    private bool _toonEmailLog;
    private int _emailVerwerkt, _emailFouten, _emailBuitenScope, _emailGeenAntwoord;

    // Wacht op beoordeling: alle berichten met status Review, ongeacht leeftijd (niet beperkt tot de laatste 24u).
    // Een door de zekerheidspoort tegengehouden mail is als gelezen gemarkeerd en komt dus nergens anders meer langs.
    private EmailLogResponse? _reviewLog;
    private bool _toonReview;
    private int _emailReview => _reviewLog?.Items.Count ?? 0;
    private string _emailReviewTekst => _reviewLog is { Items.Count: >= EmailReviewMax } ? $"{EmailReviewMax}+" : _emailReview.ToString();
    private const int EmailReviewMax = 200;

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

        var settingsTask = InstellingenCache.GetSettingsAsync(true);
        var syncTask    = InstellingenApi.GetSyncStatusAsync();
        var emailTask   = EmailApi.GetEmailLogAsync(vanaf: DateTime.Today.AddDays(-1), limit: 200);
        var reviewTask  = EmailApi.GetEmailLogAsync(status: "Review", limit: EmailReviewMax);

        await Task.WhenAll(settingsTask, syncTask, emailTask, reviewTask);

        var reviewResult = await reviewTask;
        _reviewLog = reviewResult.Success ? reviewResult.Data : null;

        var r = await settingsTask;
        if (r.Success)
        {
            settings = r.Data;
            if (settings != null)
            {
                // Een ontbrekende of ongeldige waarde toont de standaard (juli), nooit een lege keuzelijst.
                settings.SeasonStartMonth = SeizoenStartMaand.OfStandaard(settings.SeasonStartMonth);
                _opgeslagenStartMaand = settings.SeasonStartMonth.Value;
            }
        }
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

    private static int HuidigSeizoenStartjaar() => SeizoenKeuze.HuidigStartjaar(DateTime.Today);

    private IEnumerable<int> ResetSeizoenOpties => SeizoenKeuze.ResetOpties(DateTime.Today);

    private void ToonResetBevestiging()
    {
        _resetSeizoen = HuidigSeizoenStartjaar();
        _toonResetBevestiging = true;
    }

    private void AnnuleerReset() => _toonResetBevestiging = false;

    private async Task BevestigResetAsync()
    {
        _toonResetBevestiging = false;
        await StartSyncAsync(_resetSeizoen);
    }

    private async Task TriggerSyncAsync() => await StartSyncAsync(null);

    private async Task StartSyncAsync(int? seizoenStartjaar)
    {
        _syncing = true;
        _syncResult = null;
        var r = await InstellingenApi.TriggerSyncAsync(seizoenStartjaar);
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
            var statusResult = await InstellingenApi.GetSyncStatusAsync(jobId);
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
        => string.IsNullOrWhiteSpace(cron) ? "Onbekend schema" : CronLeesbaar.Vertaal(cron);

    private async Task LaadUitgeslotenEmailsAsync()
    {
        var r = await EmailApi.GetUitgeslotenEmailsAsync();
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
        var r = await EmailApi.CreateUitgeslotenEmailAsync(nieuwAdres);
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
        var r = await EmailApi.DeleteUitgeslotenEmailAsync(id);
        uitgeslotenMessage = r.Success ? null : "Fout: " + r.ErrorMessage;
        await LaadUitgeslotenEmailsAsync();
    }

    private async Task ZoekCoordinaten()
    {
        if (settings == null || string.IsNullOrWhiteSpace(settings.AccommodatiePlaats)) return;
        geocoding = true;
        geocodeMessage = null;
        geocodeError = false;
        var r = await InstellingenApi.GeocodeAsync(settings.AccommodatiePlaats);
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

    private SettingsUpdateDto BouwUpdate(AppSettingsDto s)
    {
        var update = new SettingsUpdateDto
        {
            Velden = new()
            {
                ["Accommodatie"] = s.Accommodatie,
                ["AccommodatiePlaats"] = s.AccommodatiePlaats,
                ["AccommodatieLatitude"] = s.AccommodatieLatitude?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["AccommodatieLongitude"] = s.AccommodatieLongitude?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["HerplanDeadlineDagen"] = s.HerplanDeadlineDagen?.ToString(),
                ["BufferMinuten"] = s.BufferMinuten?.ToString(),
                ["PlannerAfzenderNaam"] = s.PlannerAfzenderNaam,
                ["PlannerEmailAdres"] = s.PlannerEmailAdres,
                ["CoordinatorNaam"] = s.CoordinatorNaam,
                ["CoordinatorFunctie"] = s.CoordinatorFunctie,
                ["FetchSchedule"] = s.FetchSchedule,
                ["UseRealtimeApi"] = s.UseRealtimeApi ? "1" : "0",
                ["KnvbPdfBijlageIngeschakeld"] = s.KnvbPdfBijlageIngeschakeld ? "1" : "0",
                ["KnvbStandaardRegio"] = s.KnvbStandaardRegio,
                ["PdfExportIngeschakeld"] = s.PdfExportIngeschakeld ? "1" : "0",
                ["ZekerheidspoortActief"] = s.ZekerheidspoortActief ? "1" : "0",
            }
        };
        if (StartMaandGewijzigd)
            update.Velden["SeasonStartMonth"] = SeizoenStartMaand.OfStandaard(s.SeasonStartMonth).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return update;
    }

    private async Task OpslaanAsync()
    {
        if (settings == null) return;
        saving = true;
        successMessage = null;
        herstartMessage = null;
        errorMessage = null;

        var r = await InstellingenCache.UpdateSettingsAsync(BouwUpdate(settings));
        if (r.Success)
        {
            successMessage = "Instellingen opgeslagen.";
            if (settings.SeasonStartMonth is int opgeslagen) _opgeslagenStartMaand = opgeslagen;
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
