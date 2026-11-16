using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorAdmin.Pages;

/// <summary>Browser layout and selection behavior for the Veld optimalisatie two-pane view.</summary>
public partial class VeldOptimalisatie
{
    private readonly GanttSelectieState _selectie = new();
    private ElementReference _layoutElement;
    private ElementReference _splitterElement;
    private ElementReference _matchListElement;
    private bool _scrollSelectieNaRender;

    private bool IsOptimaal => _visTab == "optimaal";
    private bool IsSelectedOutsideFilter => _plan != null
        && _selectie.WedstrijdCode.HasValue
        && !GefilterdeBasisLijst().Any(w => w.WedstrijdCode == _selectie.WedstrijdCode);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JS.InvokeVoidAsync("blazorHelpers.initializeVeldOptimalisatie", _layoutElement, _splitterElement);
        }

        if (_scrollSelectieNaRender && _selectie.WedstrijdCode.HasValue)
        {
            _scrollSelectieNaRender = false;
            await JS.InvokeVoidAsync("blazorHelpers.scrollWedstrijdInLijst", _matchListElement,
                $"wedstrijd-{_selectie.WedstrijdCode.Value}");
        }
    }

    // DOM resources are released by the client observer when this layout is removed.
    // The inherited IDisposable still releases the ClubSelectorPageBase subscription.

    // Sortering staat sinds #1331 in BlazorAdmin/Services/DagplanningSortering.cs — tijd, dan de
    // interne clubveldvolgorde (OptimaalVeldNummer).
    private IEnumerable<AutoPlanWedstrijdItemDto> GefilterdeLijst()
    {
        if (_plan == null) return Enumerable.Empty<AutoPlanWedstrijdItemDto>();
        var gefilterd = GefilterdeBasisLijst();
        return DagplanningSortering.Sorteer(_selectie.VoegGeselecteerdeToe(
            gefilterd, _plan.Wedstrijden, w => w.WedstrijdCode));
    }

    private IEnumerable<AutoPlanWedstrijdItemDto> GefilterdeBasisLijst()
    {
        if (_plan == null) return Enumerable.Empty<AutoPlanWedstrijdItemDto>();
        return _filter switch
        {
            "wijzigingen" => _plan.Wedstrijden.Where(w => w.Status is "nieuw-slot" or "wijziging"),
            "probleem" => _plan.Wedstrijden.Where(w => w.Status == "niet-inplanbaar"),
            _ => _plan.Wedstrijden
        };
    }

    private Task SelecteerWedstrijd(long wedstrijdCode)
    {
        _selectie.Selecteer(wedstrijdCode);
        _scrollSelectieNaRender = true;
        return Task.CompletedTask;
    }
}
