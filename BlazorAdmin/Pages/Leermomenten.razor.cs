using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class Leermomenten : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private List<LeermomentDto> _items = new();
    private LeermomentenStatsDto? _stats;
    private string _filterStatus = "pending";
    private bool _bezig;
    private string? _fout;
    private readonly HashSet<int> _actieBezigId = new();

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
            var statsTask = Api.GetLeermomentenStatsAsync();
            var itemsTask = Api.GetLeermomentenAsync(string.IsNullOrEmpty(status) ? null : status);
            await Task.WhenAll(statsTask, itemsTask);

            var statsResult = await statsTask;
            var itemsResult = await itemsTask;

            if (statsResult.Success) _stats = statsResult.Data;
            if (itemsResult.Success)
                _items = itemsResult.Data?.Items ?? new();
            else
                _fout = itemsResult.ErrorMessage ?? "Ophalen mislukt";
        }
        catch (Exception ex)
        {
            _fout = ex.Message;
        }
        finally
        {
            _bezig = false;
        }
    }

    private async Task ValideerAsync(int id, string actie)
    {
        _actieBezigId.Add(id);
        StateHasChanged();

        try
        {
            var result = await Api.ValideerLeermomentAsync(id, actie);
            if (!result.Success)
                _fout = result.ErrorMessage ?? "Actie mislukt";
            else
                await LaadAsync(_filterStatus);
        }
        catch (Exception ex)
        {
            _fout = ex.Message;
        }
        finally
        {
            _actieBezigId.Remove(id);
        }
    }
}
