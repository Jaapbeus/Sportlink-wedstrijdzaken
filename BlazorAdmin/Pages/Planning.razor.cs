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
    [Inject] private AdminApiClient Api { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private DateTime _datumDt;

    /// <summary>#1468: het inline aanmaakformulier is open.</summary>
    private bool _toonAanmaken;
    private void ToggleAanmaken() => _toonAanmaken = !_toonAanmaken;

    // Directe veldbezetting (#566)
    private List<VeldbezettingItemDto> _veldbezetting = new();
    private bool _veldbezettingBezig;
    private string? _veldbezettingError;

    // Hover-correlatie tussen tijdlijnblok en tabelregel (#1315, generiek gemaakt bij #1398):
    // dezelfde WedstrijdCode licht in beide op, zodat een wedstrijd uit de lijst visueel terug te
    // vinden is in de tijdlijn erboven. Gedeelde implementatie, ook gebruikt door VeldOptimalisatie.
    private readonly GanttHoverState _hover = new();

    // Sportlink-kolom (#989/#991/#1361): alleen de vlag blijft hier; uitklap-/deeplinkstate staat in
    // SportlinkActieKolomState, het paneel zelf is SportlinkMatchPanel (#1122).
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
        var settings = await Api.GetSettingsAsync();
        _sportlinkExtensionEnabled = settings.Success && settings.Data?.SportlinkExtensionEnabled == true;
    }

    private async Task OnDatumChanged() => await LoadVeldbezettingAsync();

    private async Task LoadVeldbezettingAsync()
    {
        _veldbezettingBezig = true;
        _veldbezettingError = null;
        try
        {
            var result = await Api.GetVeldbezettingAsync(DatumStr);
            if (result.Success)
                _veldbezetting = result.Data ?? new();
            else
            {
                _veldbezetting = new();
                _veldbezettingError = result.ErrorMessage ?? "Ophalen veldbezetting mislukt.";
            }
        }
        finally { _veldbezettingBezig = false; }
    }

    protected override async Task OnClubChangedAsync() => await LoadVeldbezettingAsync();

    // Gantt-blokken voor de directe veldbezetting-weergave (#566) — geen optimalisatie, alleen wat er
    // al gepland staat. Hergebruikt dezelfde GanttItem/helpers als Veld optimalisatie.
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
                "ongewijzigd", w.DuurMinuten, null, null, WedstrijdCode: w.WedstrijdCode));
        }
        return items;
    }
}
