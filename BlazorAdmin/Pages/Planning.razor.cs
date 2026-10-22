using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorAdmin.Pages;

/// <summary>Code-behind van <c>Planning.razor</c> (#1361, afgesplitst van het toenmalige
/// <c>Dagplanning.razor</c>): datum-invoer en de directe veldbezetting — wat er nu al in Sportlink
/// gepland staat. De Gantt-weergavehelpers staan gedeeld in <see cref="DagplanningWeergaveHelpers"/>
/// (ook gebruikt door <c>VeldOptimalisatie.razor.cs</c>); de Sportlink-uitklap-/deeplinkstate in
/// <see cref="SportlinkActieKolomState"/>.</summary>
public partial class Planning : ClubSelectorPageBase
{
    [Inject] private ClubInstellingenCache InstellingenCache { get; set; } = default!;
    [Inject] private PlanningApiClient PlanningApi { get; set; } = default!;
    [Inject] private SportlinkApiClient SportlinkApi { get; set; } = default!;
    [Inject] private IAuthService Auth { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private DateTime _datumDt;

    /// <summary>#1468: het inline aanmaakformulier is open.</summary>
    private bool _toonAanmaken;
    private void ToggleAanmaken() => _toonAanmaken = !_toonAanmaken;

    // Directe veldbezetting (#566)
    private List<VeldbezettingItemDto> _veldbezetting = new();
    private SportlinkVeldplannerStatusDto? _sportlinkStatus;
    private bool _veldbezettingBezig;
    private string? _veldbezettingError;

    // Hover-correlatie tussen tijdlijnblok en tabelregel (#1315, generiek gemaakt bij #1398):
    // dezelfde WedstrijdCode licht in beide op, zodat een wedstrijd uit de lijst visueel terug te
    // vinden is in de tijdlijn erboven. Gedeelde implementatie, ook gebruikt door VeldOptimalisatie.
    private readonly GanttHoverState _hover = new();

    // Sportlink-kolom (#989/#991/#1361): alleen de vlag blijft hier; uitklap-/deeplinkstate staat in
    // SportlinkActieKolomState, het paneel zelf is SportlinkMatchPanel (#1122).
    private bool _pdfExportIngeschakeld;
    private bool _sportlinkExtensionEnabled;
    private readonly SportlinkActieKolomState _sportlinkKolom = new();

    private string ExportBestandsNaam => $"veldbezetting-{DatumStr}";
    // Club zit in de sleutel: na een clubwissel moet de preview opnieuw worden opgehaald (#1461).
    private string ExportSleutel => $"{ClubSelector.SelectedClubCode}|{DatumStr}";
    private string DatumStr => _datumDt.ToString("yyyy-MM-dd");

    protected override void OnInitialized()
    {
        base.OnInitialized();
        _datumDt = DagplanningWeergaveHelpers.VolgendeZaterdag().ToDateTime(TimeOnly.MinValue);
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadVeldbezettingAsync();

        // #989: geen Sportlink-kolom/-knoppen tonen als de extension uit staat (DoD).
        var settings = await InstellingenCache.GetSettingsAsync();
        _sportlinkExtensionEnabled = settings.Success && settings.Data?.SportlinkExtensionEnabled == true;
        await LaadPdfExportStatusAsync();
    }

    private async Task OnDatumChanged() => await LoadVeldbezettingAsync();

    private async Task LoadVeldbezettingAsync()
    {
        _veldbezettingBezig = true;
        _veldbezettingError = null;
        try
        {
            var result = await PlanningApi.GetVeldbezettingAsync(DatumStr);
            if (result.Success)
            {
                _veldbezetting = result.Data?.Wedstrijden ?? new();
                _sportlinkStatus = result.Data?.Sportlink;
            }
            else
            {
                _veldbezetting = new();
                _sportlinkStatus = null;
                _veldbezettingError = result.ErrorMessage ?? "Ophalen veldbezetting mislukt.";
            }
        }
        finally { _veldbezettingBezig = false; }
    }

    protected override async Task OnClubChangedAsync()
    {
        await LoadVeldbezettingAsync();
        await LaadPdfExportStatusAsync();
    }

    /// <summary>
    /// #1459: de PDF-knop verschijnt alleen als de gekozen club PDF-export heeft ingeschakeld
    /// (standaard uit). Via het voor elke ingelogde rol open endpoint — niet via de admin-only
    /// instellingen — en opnieuw bij een clubwissel.
    /// </summary>
    private async Task LaadPdfExportStatusAsync() => _pdfExportIngeschakeld = await PlanningApi.IsPdfExportIngeschakeldAsync();

    // Gantt-blokken voor de directe veldbezetting-weergave (#566) — geen optimalisatie, alleen wat er
    // al gepland staat. Hergebruikt dezelfde GanttItem/helpers als Veld optimalisatie.
    /// <summary>Eigen regels die Sportlink niet kent (#1582): een zichtbare afwijking tussen onze gegevens en de veldplanner.</summary>
    /// <summary>#1774: de rode "Sportlink niet geraadpleegd"-melding (nooit voor de demo-club).</summary>
    private bool ToonSportlinkWaarschuwing => DagplanningWeergaveHelpers.ToonSportlinkWaarschuwing(_sportlinkStatus);

    /// <summary>#1774: tijdstip van de raadpleging in lokale tijd, voor de bronregel.</summary>
    private string? SportlinkBronTekst => _sportlinkStatus is { Geraadpleegd: true, GeraadpleegdOpUtc: { } utc }
        ? $"Bron: Sportlink-veldplanner, geraadpleegd om {DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime():HH:mm}"
        : null;

    private int AantalNietInSportlink => _veldbezetting.Count(w => w.NietInSportlink);

    private List<DagplanningWeergaveHelpers.GanttItem> BouwVeldbezettingGanttItems()
    {
        var items = new List<DagplanningWeergaveHelpers.GanttItem>();
        foreach (var w in _veldbezetting.Where(w => w.DuurMinuten > 0))
        {
            if (string.IsNullOrWhiteSpace(w.Veld) || string.IsNullOrWhiteSpace(w.AanvangsTijd)) continue;
            if (!TimeOnly.TryParse(w.AanvangsTijd, out var t)) continue;
            var (veldBase, sub) = DagplanningWeergaveHelpers.GanttSplitVeld(w.Veld);
            items.Add(new DagplanningWeergaveHelpers.GanttItem(veldBase, sub, t, t.AddMinutes(w.DuurMinuten),
                w.Veldafmeting, DagplanningWeergaveHelpers.GanttMatchLabel(w.Wedstrijd, w.TeamNaam),
                w.NietInSportlink ? "wijziging" : "ongewijzigd", w.DuurMinuten, null, null, WedstrijdCode: w.WedstrijdCode));
        }
        return items;
    }
}
