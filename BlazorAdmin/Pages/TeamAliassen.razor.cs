using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class TeamAliassen : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private List<TeamAliasDto> _items = new();
    private TeamAliassenResponse? _stats;
    private string _filterStatus = "pending";
    private bool _bezig;
    private string? _fout;
    private string? _melding;
    private int? _verwijderConfirmId;
    private readonly HashSet<int> _actieBezigId = new();
    private bool _herstelBezig;
    private TeamHerstelDto? _herstelResultaat;
    private string? _herstelFout;

    protected override async Task OnInitializedAsync()
    {
        await LaadAsync("pending");
    }

    protected override Task OnClubChangedAsync() => LaadAsync(_filterStatus);

    /// <summary>
    /// Bouwt de canonieke teamlijst opnieuw op (#946) en herlaadt daarna de aliaslijst, omdat de
    /// canonicalisatie zelf gevalideerde Sync-aliassen bijwerkt.
    /// </summary>
    private async Task HerstelTeamlijstAsync()
    {
        _herstelBezig = true;
        _herstelResultaat = null;
        _herstelFout = null;
        StateHasChanged();

        try
        {
            var result = await Api.HerstelTeamlijstAsync();
            if (result.Success && result.Data != null)
            {
                _herstelResultaat = result.Data;
                await LaadAsync(_filterStatus);
            }
            else
            {
                // Een 409 betekent: er is nog niets gesynchroniseerd, dus er valt niets af te leiden.
                // Dat is geen storing maar een stand van zaken — vandaar een waarschuwing en geen fout.
                _herstelFout = result.ErrorMessage ?? "Opnieuw opbouwen mislukt";
            }
        }
        catch (Exception ex)
        {
            _herstelFout = ex.Message;
        }
        finally
        {
            _herstelBezig = false;
            StateHasChanged();
        }
    }

    private async Task LaadAsync(string status)
    {
        _filterStatus = status;
        _bezig = true;
        _fout = null;
        _verwijderConfirmId = null;
        StateHasChanged();

        try
        {
            var result = await Api.GetTeamAliassenAsync(string.IsNullOrEmpty(status) ? null : status);
            if (result.Success && result.Data != null)
            {
                _stats = result.Data;
                _items = result.Data.Items;
            }
            else
            {
                _fout = result.ErrorMessage ?? "Ophalen mislukt";
            }
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

    private async Task ValideerAsync(int id, string status)
    {
        _actieBezigId.Add(id);
        _melding = null;
        StateHasChanged();

        try
        {
            var result = await Api.ValideerTeamAliasAsync(id, status);
            if (!result.Success)
                _fout = result.ErrorMessage ?? "Actie mislukt";
            else
            {
                _melding = status == "validated" ? "Alias goedgekeurd." : "Alias afgewezen.";
                await LaadAsync(_filterStatus);
            }
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

    private async Task VerwijderAsync(int id)
    {
        _actieBezigId.Add(id);
        _melding = null;
        StateHasChanged();

        try
        {
            var result = await Api.DeleteTeamAliasAsync(id);
            if (!result.Success)
                _fout = result.ErrorMessage ?? "Verwijderen mislukt";
            else
            {
                _melding = "Alias verwijderd.";
                await LaadAsync(_filterStatus);
            }
        }
        catch (Exception ex)
        {
            _fout = ex.Message;
        }
        finally
        {
            _actieBezigId.Remove(id);
            _verwijderConfirmId = null;
        }
    }

    private static string BronLabel(string bron) => bron switch
    {
        "Sync"                 => "Sportlink-sync",
        "AiDisambiguatie"      => "AI-keuze",
        "CoordinatorCorrectie" => "Correctie coördinator",
        _                      => bron
    };
}
