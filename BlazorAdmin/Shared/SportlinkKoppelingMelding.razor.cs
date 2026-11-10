using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

/// <summary>
/// Code-behind van <c>SportlinkKoppelingMelding.razor</c> (#1773): haalt bij het laden één keer de
/// koppelingsstatus op (<c>GET /api/sportlink/koppeling-status</c>, eigen database, geen
/// Sportlink-aanroep) en toont alleen iets bij een verbroken of haperende koppeling. Een mislukte
/// ophaalactie geeft bewust geen melding: de pagina mag niet rood kleuren door een statusfout.
/// </summary>
public partial class SportlinkKoppelingMelding
{
    /// <summary>Extra CSS-klassen voor de alert, bijv. <c>mb-3</c>.</summary>
    [Parameter] public string Klasse { get; set; } = "mb-3";

    [Inject] private SportlinkApiClient SportlinkApi { get; set; } = default!;

    private SportlinkKoppelingStatusDto? _status;

    protected override async Task OnInitializedAsync()
    {
        var r = await SportlinkApi.GetSportlinkKoppelingStatusAsync();
        _status = r.Success ? r.Data : null;
    }
}
