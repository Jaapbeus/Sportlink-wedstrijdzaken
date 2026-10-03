using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

/// <summary>Code-behind van <c>WedstrijdAanmakenFormulier.razor</c> (#997/#1116, code-behind sinds #1122; gedeeld component sinds #1468).</summary>
public partial class WedstrijdAanmakenFormulier
{
    /// <summary>Begindatum van het formulier (bijv. de gekozen datum op Planning); zonder waarde: vandaag.</summary>
    [Parameter] public DateTime? BeginDatum { get; set; }

    /// <summary>Wordt aangeroepen na een echte aanmaak (geen dry-run) en na een geslaagde verwijdering.</summary>
    [Parameter] public EventCallback OnAangemaakt { get; set; }

    /// <summary>Toon de inleidende alinea (Planning verbergt hem).</summary>
    [Parameter] public bool ToonIntro { get; set; } = true;

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
    private List<OefenwedstrijdAgeClassDto> _ageClasses = new();

    /// <summary>#1437: alle invoer, met de voorinvulling bij een teamkeuze en "Leegmaken" (testbaar, los van deze pagina).</summary>
    private readonly OefenwedstrijdFormulierState _form = new();

    private readonly SportlinkActieStatus _status = new();
    private OefenwedstrijdResultaatDto? _resultaat;

    /// <summary>#1436: Enter is geen aanmaak meer, en zonder bevestigde dry-run vraagt de pagina eerst bevestiging.</summary>
    private readonly WedstrijdAanmaakPoort _poort = new();
    private IReadOnlyList<string> _validatieFouten = Array.Empty<string>();

    /// <summary>#1440: verwijderen van de zojuist aangemaakte wedstrijd — altijd met bevestiging, eigen status.</summary>
    private readonly WedstrijdVerwijderPoort _verwijderPoort = new();
    private readonly SportlinkActieStatus _verwijderStatus = new();

    /// <summary>Naam van het gekozen veld voor de bevestigtekst, of <c>null</c> zonder veld.</summary>
    private string? GekozenVeldNaam => _velden.FirstOrDefault(v => v.VeldNummer == _form.VeldNummer)?.VeldNaam;

    private string GekozenVelddeelLabel =>
        OefenwedstrijdFormulierState.Velddelen.FirstOrDefault(v => v.Waarde == _form.Velddeel)?.Label ?? "Heel veld";

    private static string VelddeelLabel(string? waarde) =>
        OefenwedstrijdFormulierState.Velddelen.FirstOrDefault(v => v.Waarde == waarde)?.Label ?? "—";

    /// <summary>Link naar de zojuist aangemaakte wedstrijd in Sportlink Club; alleen na een echte (niet-dry-run) aanmaak.</summary>
    private string? SportlinkLink =>
        _resultaat is { IsSuccess: true, IsDryRun: false, PublicMatchId: { Length: > 0 } id }
            ? OefenwedstrijdFormulierState.SportlinkWedstrijdUrl(id)
            : null;

    protected override async Task OnInitializedAsync()
    {
        _form.Leegmaken(BeginDatum);
        var teamsTaak = Api.GetTeamsAsync();
        var veldenTaak = Api.GetVeldenAsync();
        var dryRunTaak = Api.GetSportlinkDryRunStatusAsync();
        var formulierTaak = Api.GetOefenwedstrijdFormulierAsync();
        await Task.WhenAll(teamsTaak, veldenTaak, dryRunTaak, formulierTaak);

        var dryRun = dryRunTaak.Result;
        _dryRun = dryRun.Success ? dryRun.Data?.DryRun : null;

        var teams = teamsTaak.Result;
        var velden = veldenTaak.Result;
        var formulier = formulierTaak.Result;
        _teams = teams.Success ? teams.Data ?? new() : new();
        _velden = velden.Success ? (velden.Data ?? new()).Where(v => v.Actief).ToList() : new();

        // #1437: de voorinvulling is een gemak, geen vereiste — faalt het formulier-endpoint, dan werkt de pagina
        // met de teamlijst en zonder voorinvulling zoals voorheen.
        if (formulier.Success && formulier.Data != null)
        {
            _form.ZetTeamGegevens(formulier.Data.Teams);
            _ageClasses = formulier.Data.AgeClasses;
            if (_teams.Count == 0) _teams = formulier.Data.Teams.Select(t => t.TeamNaam).ToList();
        }

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
        WisVerwijderen();
        _validatieFouten = WedstrijdAanmaakPoort.Valideer(_form.Datum, _form.Tijd, _form.Duur, _form.TeamNaam, _form.Tegenstander, _form.Velddeel);
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

    /// <summary>Klik op "Leegmaken" (#1437): alle velden terug naar de beginstand, ook meldingen en resultaat.</summary>
    private void Leegmaken()
    {
        if (_poort.InvoerVergrendeld || _verwijderPoort.Huidig == WedstrijdVerwijderPoort.Stap.Bezig) return;
        _form.Leegmaken(BeginDatum);
        _validatieFouten = Array.Empty<string>();
        _resultaat = null;
        _status.Wis();
        WisVerwijderen();
    }

    private void WisVerwijderen()
    {
        _verwijderPoort.Reset();
        _verwijderStatus.Wis();
    }

    /// <summary>Klik op "Wedstrijd verwijderen uit Sportlink" (#1440): alleen de bevestigstap tonen.</summary>
    private void VraagVerwijderenAan()
    {
        _verwijderStatus.Wis();
        _verwijderPoort.VraagAan();
    }

    private void AnnuleerVerwijderen() => _verwijderPoort.Annuleer();

    /// <summary>Klik op "Ja, verwijderen" in de bevestigstap.</summary>
    private async Task BevestigVerwijderenAsync()
    {
        var publicMatchId = _resultaat?.PublicMatchId;
        if (string.IsNullOrEmpty(publicMatchId) || !_verwijderPoort.Bevestig()) return;

        _verwijderStatus.Start();
        StateHasChanged();
        SportlinkMutatieResultaatDto? data = null;
        try
        {
            var r = await Api.DeleteOefenwedstrijdAsync(publicMatchId);
            data = r.Success ? r.Data : null;
            var geslaagd = _verwijderStatus.Verwerk(r.Success, r.ErrorMessage, r.Data, "Wedstrijd verwijderd uit Sportlink Club.", "Sportlink heeft het verwijderen afgewezen");
            if (geslaagd && !_verwijderStatus.IsDryRun)
                await OnAangemaakt.InvokeAsync();
        }
        finally
        {
            _verwijderStatus.Klaar();
            _verwijderPoort.Klaar(data);
        }
    }

    private async Task VerstuurAsync()
    {
        _status.Start();
        StateHasChanged();
        try
        {
            var r = await Api.PostOefenwedstrijdAsync(
                _form.Datum.Date + TimeSpan.Parse(_form.Tijd!), _form.Duur, _form.TeamNaam!, _form.IsVrijeTekst,
                _form.Tegenstander!, _form.VeldNummer, _form.Omschrijving, _form.Velddeel, _form.AgeClassCode);
            _resultaat = r.Success ? r.Data : null;
            var geslaagd = _status.Verwerk(r.Success, r.ErrorMessage, r.Data, "Wedstrijd aangemaakt in Sportlink Club.", "Sportlink heeft de aanmaak afgewezen");
            if (geslaagd && _resultaat?.IsDryRun == false)
            {
                _form.NaGeslaagdeAanmaak();
                await OnAangemaakt.InvokeAsync();
            }
        }
        finally
        {
            _status.Klaar();
            _poort.Klaar();
        }
    }
}
