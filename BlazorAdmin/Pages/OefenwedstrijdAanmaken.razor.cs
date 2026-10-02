using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

/// <summary>Code-behind van <c>OefenwedstrijdAanmaken.razor</c> (#997/#1116, code-behind sinds #1122).</summary>
public partial class OefenwedstrijdAanmaken
{
    /// <summary>Sentinel-waarde van de "Vrije tekst"-optie in de Team-dropdown (#1396).</summary>
    private const string VrijeTekstOptie = "__vrije-tekst__";

    /// <summary>Banner bij dry-run aan / uit (#1427) — één zin, afhankelijk van de actuele instelling.</summary>
    internal const string DryRunAanTekst = "Dryrun is aan. Geen data wordt naar Sportlink geschreven.";
    internal const string DryRunUitTekst = "Dryrun is uit. Wijzigingen worden direct in Sportlink weggeschreven.";

    [Inject] private AdminApiClient Api { get; set; } = default!;

    /// <summary>Actuele dry-run-stand; <c>null</c> zolang onbekend (dan geen banner).</summary>
    private bool? _dryRun;

    private bool _laden = true;
    private string? _laadFout;
    private List<string> _teams = new();
    private List<VeldDto> _velden = new();

    private DateTime _datum = DateTime.Today;
    private string? _tijd = "19:00";
    private int _duur = 90;
    private string? _teamSelectie;
    private string? _teamVrijeTekst;
    private string? _tegenstander;
    private int? _veldNummer;
    private string? _omschrijving;

    private readonly SportlinkActieStatus _status = new();
    private OefenwedstrijdResultaatDto? _resultaat;

    /// <summary>#1436: Enter is geen aanmaak meer, en zonder bevestigde dry-run vraagt de pagina eerst bevestiging.</summary>
    private readonly WedstrijdAanmaakPoort _poort = new();
    private IReadOnlyList<string> _validatieFouten = Array.Empty<string>();

    /// <summary>De daadwerkelijk te gebruiken teamnaam: uit de dropdown, of het vrije-tekstveld
    /// als de gebruiker "Vrije tekst" heeft gekozen (#1396).</summary>
    private string? TeamNaam => _teamSelectie == VrijeTekstOptie ? _teamVrijeTekst : _teamSelectie;

    /// <summary>Naam van het gekozen veld voor de bevestigtekst, of <c>null</c> zonder veld.</summary>
    private string? GekozenVeldNaam => _velden.FirstOrDefault(v => v.VeldNummer == _veldNummer)?.VeldNaam;

    private string StandaardOmschrijving =>
        string.IsNullOrWhiteSpace(TeamNaam) || string.IsNullOrWhiteSpace(_tegenstander)
            ? "standaard: Oefenwedstrijd [team] - [tegenstander]"
            : $"Oefenwedstrijd {TeamNaam} - {_tegenstander}";

    protected override async Task OnInitializedAsync()
    {
        var teamsTaak = Api.GetTeamsAsync();
        var veldenTaak = Api.GetVeldenAsync();
        var dryRunTaak = Api.GetSportlinkDryRunStatusAsync();
        await Task.WhenAll(teamsTaak, veldenTaak, dryRunTaak);

        var dryRun = dryRunTaak.Result;
        _dryRun = dryRun.Success ? dryRun.Data?.DryRun : null;

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

    /// <summary>Klik op "Wedstrijd aanmaken": valideren, en bij dry-run uit eerst de bevestigstap tonen (#1436).</summary>
    private async Task VraagAanAsync()
    {
        _status.Wis();
        _resultaat = null;
        _validatieFouten = WedstrijdAanmaakPoort.Valideer(_datum, _tijd, _duur, TeamNaam, _tegenstander);
        if (_poort.VraagAan(_validatieFouten, _dryRun))
            await VerstuurAsync();
    }

    /// <summary>Klik op "Bevestigen en aanmaken" in de bevestigstap.</summary>
    private async Task BevestigAsync()
    {
        if (_poort.Bevestig())
            await VerstuurAsync();
    }

    private void Annuleer() => _poort.Annuleer();

    private async Task VerstuurAsync()
    {
        _status.Start();
        StateHasChanged();
        try
        {
            var r = await Api.PostOefenwedstrijdAsync(_datum.Date + TimeSpan.Parse(_tijd!), _duur, TeamNaam!, _teamSelectie == VrijeTekstOptie, _tegenstander!, _veldNummer, _omschrijving);
            _resultaat = r.Success ? r.Data : null;
            var geslaagd = _status.Verwerk(r.Success, r.ErrorMessage, r.Data, "Wedstrijd aangemaakt in Sportlink Club.", "Sportlink heeft de aanmaak afgewezen");
            if (geslaagd && _resultaat?.IsDryRun == false)
            {
                _tegenstander = null;
                _omschrijving = null;
            }
        }
        finally
        {
            _status.Klaar();
            _poort.Klaar();
        }
    }
}
