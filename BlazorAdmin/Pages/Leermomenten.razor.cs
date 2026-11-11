using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class Leermomenten : ClubSelectorPageBase
{
    [Inject] private LerenApiClient Api { get; set; } = default!;

    private List<LeermomentDto> _items = new();
    private LeermomentenStatsDto? _stats;
    private string _filterStatus = "pending";
    private bool _bezig;
    private string? _fout;
    private readonly HashSet<int> _actieBezigId = new();

    private readonly LookupGeneratieGuard _laadGuard = new();

    private bool _toevoegenOpen;

    private void WisselToevoegen() => _toevoegenOpen = !_toevoegenOpen;

    private async Task NaToevoegenAsync()
    {
        _toevoegenOpen = false;
        await LaadAsync(_filterStatus);
    }

    protected override async Task OnInitializedAsync()
    {
        await LaadAsync("pending");
    }

    protected override Task OnClubChangedAsync() => LaadAsync(_filterStatus ?? "pending");

    private async Task LaadAsync(string status)
    {
        _filterStatus = status;
        _bezig = true;
        _fout = null;
        StateHasChanged();

        try
        {
            // #1872: een late response van de vorige club mag de lijst/stats van de huidige club niet overschrijven.
            var toegepast = await LaadClubgebondenAsync(_laadGuard, async () =>
            {
                var statsTask = Api.GetLeermomentenStatsAsync();
                var itemsTask = Api.GetLeermomentenAsync(string.IsNullOrEmpty(status) ? null : status);
                await Task.WhenAll(statsTask, itemsTask);
                return (Stats: await statsTask, Items: await itemsTask);
            }, r =>
            {
                if (r.Stats.Success) _stats = r.Stats.Data;
                if (r.Items.Success)
                    _items = r.Items.Data?.Items ?? new();
                else
                    _fout = r.Items.ErrorMessage ?? "Ophalen mislukt";
            });
            if (toegepast) _bezig = false;
        }
        catch (Exception ex)
        {
            _fout = ex.Message;
            _bezig = false;
        }
    }

    private int? _verwijderBevestigId;

    private void VraagVerwijderen(int id) => _verwijderBevestigId = id;

    private void AnnuleerVerwijderen() => _verwijderBevestigId = null;

    private async Task VerwijderAsync(int id)
    {
        _actieBezigId.Add(id);
        StateHasChanged();
        var clubCode = ClubSelector.SelectedClubCode;
        try
        {
            var result = await Api.VerwijderLeermomentAsync(id);
            _verwijderBevestigId = null;
            if (IsClubGewisseld(clubCode)) return; // #1872: uitkomst hoort bij een andere club
            if (!result.Success)
                _fout = result.ErrorMessage ?? "Verwijderen mislukt";
            else
                await LaadAsync(_filterStatus);
        }
        catch (Exception ex)
        {
            if (!IsClubGewisseld(clubCode)) _fout = ex.Message;
        }
        finally
        {
            _actieBezigId.Remove(id);
        }
    }

    private async Task ValideerAsync(int id, string actie)
    {
        _actieBezigId.Add(id);
        StateHasChanged();

        var clubCode = ClubSelector.SelectedClubCode;
        try
        {
            var result = await Api.ValideerLeermomentAsync(id, actie);
            if (IsClubGewisseld(clubCode)) return; // #1872
            if (!result.Success)
                _fout = result.ErrorMessage ?? "Actie mislukt";
            else
                await LaadAsync(_filterStatus);
        }
        catch (Exception ex)
        {
            if (!IsClubGewisseld(clubCode)) _fout = ex.Message;
        }
        finally
        {
            _actieBezigId.Remove(id);
        }
    }
}
