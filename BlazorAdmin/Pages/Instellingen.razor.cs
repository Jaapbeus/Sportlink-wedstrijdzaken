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
    private readonly LookupGeneratieGuard _loadGuard = new();
    private readonly LookupGeneratieGuard _uitgeslotenEmailsLoadGuard = new();
    private UitgeslotenEmailAdresDto? nieuwAdres;
    private string? uitgeslotenMessage;
    private readonly InlineBevestiging<(string? ClubCode, int Id)> _verwijderAdresBevestiging = new();

    // Sync + email log (verplaatst van Dashboard #344)
    private SyncStatusDto? _status;
    private EmailLogResponse? _emailLog;
    private bool _syncLoading = true;
    private bool _syncing;
    private string? _syncResult;
    private CancellationTokenSource? _syncPollCancellation;
    private int _syncPollGeneration;
    private int _syncOperationGeneration;
    private bool _disposed;
    private int _syncPollIntervalMilliseconds = 3000;
    private Task? _syncPollTask;
    private bool _toonResetBevestiging;
    private int _resetSeizoen = HuidigSeizoenStartjaar();
    private bool _toonEmailLog;
    private int _emailVerwerkt, _emailFouten, _emailBuitenScope, _emailGeenAntwoord;

    // Wacht op beoordeling: alle berichten met status Review, ongeacht leeftijd (niet beperkt tot de laatste 24u).
    // Een door de zekerheidspoort tegengehouden mail is als gelezen gemarkeerd en komt dus nergens anders meer langs.
    private EmailLogResponse? _reviewLog;
    private bool _toonReview;
    private int _emailReview => _reviewLog?.StatusCounts?.GetValueOrDefault("Review") ?? 0;
    private string _emailReviewTekst => _reviewLog?.StatusCounts is null ? "Niet beschikbaar" : _emailReview.ToString();
    private const int EmailReviewMax = 200;

    private bool _isTestmodus => ClubSelector.SelectedClubCode == "ALLSTARS";

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override async Task OnClubChangedAsync()
    {
        StopSyncPolling();
        _syncing = false;
        _status = null;
        _syncResult = null;
        _verwijderAdresBevestiging.Annuleer();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var clubCode = ClubSelector.SelectedClubCode;
        var generatie = _loadGuard.Start(clubCode);
        var syncGeneration = ++_syncOperationGeneration;
        StopSyncPolling();
        errorMessage = null;
        settings = null;
        _syncLoading = true;

        if (_isTestmodus)
        {
            StopSyncPolling();
            _status = null;
            _emailLog = null;
            _reviewLog = null;
            _emailVerwerkt = _emailFouten = _emailBuitenScope = _emailGeenAntwoord = 0;
            uitgeslotenEmails = new();
            _syncLoading = false;
            return;
        }

        var settingsTask = InstellingenCache.GetSettingsAsync(true);
        var syncTask    = InstellingenApi.GetSyncStatusAsync();
        var emailTask   = EmailApi.GetEmailLogAsync(vanaf: DateTime.UtcNow.AddHours(-24), limit: 200);
        var reviewTask  = EmailApi.GetEmailLogAsync(status: "Review", limit: EmailReviewMax);

        try { await Task.WhenAll(settingsTask, syncTask, emailTask, reviewTask); }
        catch
        {
            if (IsActueleLading(generatie, clubCode) && IsActueleSyncOperatie(syncGeneration, clubCode))
            {
                errorMessage = "Instellingen konden niet worden geladen. Probeer het later opnieuw.";
                _syncLoading = false;
            }
            return;
        }
        if (!IsActueleLading(generatie, clubCode) || !IsActueleSyncOperatie(syncGeneration, clubCode)) return;

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
        if (syncResult.Success)
        {
            _status = syncResult.Data;
            _syncing = IsActieveSync(_status?.ActiveJob);
            if (_syncing) StartSyncPolling(clubCode);
            else FinishSync(_status?.LastCompletedJob ?? _status?.Job);
        }

        var emailResult = await emailTask;
        if (emailResult.Success)
        {
            _emailLog = emailResult.Data;
            if (_emailLog?.StatusCounts != null)
            {
                _emailVerwerkt    = _emailLog.StatusCounts.GetValueOrDefault("AntwoordVerstuurd");
                // #572: verwerkt zonder automatisch antwoord — planning was mogelijk
                _emailGeenAntwoord = _emailLog.StatusCounts.GetValueOrDefault("GeenAntwoordNodig");
                _emailFouten      = _emailLog.StatusCounts.GetValueOrDefault("Fout");
                _emailBuitenScope = _emailLog.StatusCounts.GetValueOrDefault("BuitenScope");
            }
        }

        _syncLoading = false;
        await LaadUitgeslotenEmailsAsync();
    }

    private bool IsActueleLading(int generatie, string? clubCode) =>
        !_disposed && _loadGuard.IsActueel(generatie) && ClubSelector.SelectedClubCode == clubCode;

    private bool IsActueleSyncOperatie(int generatie, string? clubCode) =>
        !_disposed && generatie == _syncOperationGeneration && !IsClubGewisseld(clubCode);
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
        if (_disposed || _syncing) return;
        var clubCode = ClubSelector.SelectedClubCode;
        var generation = ++_syncOperationGeneration;
        StopSyncPolling();
        _syncing = true;
        _syncResult = null;
        var r = await InstellingenApi.TriggerSyncAsync(seizoenStartjaar);
        if (!IsActueleSyncOperatie(generation, clubCode)) return;
        if (!r.Success || r.Data?.JobId is not Guid jobId)
        {
            _syncResult = "Synchronisatie starten is niet gelukt. Probeer het later opnieuw.";
            _syncing = false;
            return;
        }
        _syncResult = r.Data.AlreadyRunning
            ? "Er loopt al een synchronisatie. De actuele voortgang wordt hieronder getoond."
            : $"Synchronisatiejob aangemaakt om {NaarLokaleTijd(r.Data.Tijdstip):HH:mm}.";
        var statusResult = await InstellingenApi.GetSyncStatusAsync(jobId);
        if (!IsActueleSyncOperatie(generation, clubCode)) return;
        if (statusResult.Success && statusResult.Data != null)
        {
            _status = statusResult.Data;
            _syncing = IsActieveSync(_status.ActiveJob);
            if (_syncing) StartSyncPolling(clubCode);
            else FinishSync(_status.LastCompletedJob ?? _status.Job);
        }
        else
        {
            _syncing = false;
            _syncResult = "Synchronisatie gestart. Voortgang is tijdelijk niet beschikbaar; ververs de pagina om opnieuw te proberen.";
        }
        StateHasChanged();
    }

    private void StartSyncPolling(string? clubCode)
    {
        StopSyncPolling();
        var cancellation = new CancellationTokenSource();
        _syncPollCancellation = cancellation;
        var generation = _syncPollGeneration;
        _syncPollTask = PollSyncStatusAsync(clubCode, generation, cancellation.Token);
    }

    private async Task PollSyncStatusAsync(string? clubCode, int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_syncPollIntervalMilliseconds, cancellationToken);
                var result = await InstellingenApi.GetSyncStatusAsync(cancellationToken: cancellationToken);
                if (cancellationToken.IsCancellationRequested || generation != _syncPollGeneration || IsClubGewisseld(clubCode)) return;
                if (!result.Success || result.Data == null) continue;

                var status = result.Data;
                _status = status;
                await InvokeAsync(StateHasChanged);
                if (cancellationToken.IsCancellationRequested || generation != _syncPollGeneration || IsClubGewisseld(clubCode) || _disposed) return;
                if (IsActieveSync(status.ActiveJob)) continue;

                FinishSync(status.LastCompletedJob ?? status.Job);
                StopSyncPolling();
                await InvokeAsync(StateHasChanged);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Clubwissel of componentverwijdering beëindigt de polling zonder UI-fout.
        }
    }

    private static bool IsActieveSync(SyncJobDto? job) => job?.Status is "pending" or "running";

    private void FinishSync(SyncJobDto? job)
    {
        if (job == null) return;
        _syncing = false;
        _syncResult = job.Status switch
        {
            "succeeded" => "Synchronisatie voltooid.",
            "partial" => "Synchronisatie gedeeltelijk voltooid. Controleer de fasen met fouten.",
            "failed" => "Synchronisatie mislukt. Controleer de fasen hieronder.",
            _ => _syncResult
        };
    }

    private void StopSyncPolling()
    {
        _syncPollGeneration++;
        var cancellation = _syncPollCancellation;
        _syncPollCancellation = null;
        if (cancellation == null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _syncing = false;
            _syncOperationGeneration++;
            _loadGuard.Start(null);
            _uitgeslotenEmailsLoadGuard.Start(null);
            StopSyncPolling();
        }
        base.Dispose(disposing);
    }

    private static string CronNaarNederlands(string? cron)
        => string.IsNullOrWhiteSpace(cron) ? "Onbekend schema" : CronLeesbaar.Vertaal(cron);

    private async Task LaadUitgeslotenEmailsAsync()
    {
        var clubCode = ClubSelector.SelectedClubCode;
        var generatie = _uitgeslotenEmailsLoadGuard.Start(clubCode);
        var r = await EmailApi.GetUitgeslotenEmailsAsync();
        if (_disposed || !_uitgeslotenEmailsLoadGuard.IsActueel(generatie) || ClubSelector.SelectedClubCode != clubCode) return;
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
        if (!_verwijderAdresBevestiging.Bevestig((ClubSelector.SelectedClubCode, id))) return;

        var r = await EmailApi.DeleteUitgeslotenEmailAsync(id);
        uitgeslotenMessage = r.Success ? null : "Fout: " + r.ErrorMessage;
        if (r.Success) await LaadUitgeslotenEmailsAsync();
    }

    private void VraagVerwijderAdres(int id) =>
        _verwijderAdresBevestiging.Vraag((ClubSelector.SelectedClubCode, id));

    private void AnnuleerVerwijderenAdres() => _verwijderAdresBevestiging.Annuleer();

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
