using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>Code-behind van <c>OefenwedstrijdAanmaken.razor</c> (#997/#1116, code-behind sinds #1122).</summary>
public partial class OefenwedstrijdAanmaken
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private bool _laden = true;
    private string? _laadFout;
    private List<string> _teams = new();
    private List<VeldDto> _velden = new();

    private DateTime _datum = DateTime.Today;
    private string? _tijd = "19:00";
    private int _duur = 90;
    private string? _teamNaam;
    private string? _tegenstander;
    private int? _veldNummer;
    private string? _omschrijving;

    private readonly SportlinkActieStatus _status = new();
    private OefenwedstrijdResultaatDto? _resultaat;

    private string StandaardOmschrijving =>
        string.IsNullOrWhiteSpace(_teamNaam) || string.IsNullOrWhiteSpace(_tegenstander)
            ? "standaard: Oefenwedstrijd [team] - [tegenstander]"
            : $"Oefenwedstrijd {_teamNaam} - {_tegenstander}";

    protected override async Task OnInitializedAsync()
    {
        var teamsTaak = Api.GetTeamsAsync();
        var veldenTaak = Api.GetVeldenAsync();
        await Task.WhenAll(teamsTaak, veldenTaak);

        var teams = teamsTaak.Result;
        var velden = veldenTaak.Result;
        _teams = teams.Success ? teams.Data ?? new() : new();
        _velden = velden.Success ? (velden.Data ?? new()).Where(v => v.Actief).ToList() : new();

        if (!teams.Success || !velden.Success)
            _laadFout = "Teams of velden konden niet worden geladen: " + (teams.ErrorMessage ?? velden.ErrorMessage ?? "onbekende fout.");
        else if (_teams.Count == 0)
            _laadFout = "Er zijn geen actieve teams bekend — voer eerst een synchronisatie uit.";

        _laden = false;
    }

    private async Task MaakAanAsync()
    {
        _status.Wis();
        _resultaat = null;

        if (string.IsNullOrWhiteSpace(_teamNaam) || string.IsNullOrWhiteSpace(_tegenstander))
        {
            _status.Fout("Kies een team en vul een tegenstander in.");
            return;
        }
        if (!TimeSpan.TryParse(_tijd, out var tijdSpan))
        {
            _status.Fout("Ongeldige aanvangstijd.");
            return;
        }

        _status.Start();
        StateHasChanged();
        try
        {
            var r = await Api.PostOefenwedstrijdAsync(_datum.Date + tijdSpan, _duur, _teamNaam, _tegenstander, _veldNummer, _omschrijving);
            _resultaat = r.Success ? r.Data : null;
            var geslaagd = _status.Verwerk(r.Success, r.ErrorMessage, r.Data, "Oefenwedstrijd aangemaakt in Sportlink Club.", "Sportlink heeft de aanmaak afgewezen");
            if (geslaagd && _resultaat?.IsDryRun == false)
            {
                _tegenstander = null;
                _omschrijving = null;
            }
        }
        finally { _status.Klaar(); }
    }
}
