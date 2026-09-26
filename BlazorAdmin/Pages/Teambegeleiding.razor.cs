using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorAdmin.Pages;

/// <summary>
/// Code-behind van <c>Teambegeleiding.razor</c> (#1122/regel 3, code-behind sinds #1322). Alleen
/// team selectie, weergave van begeleiders en "vraag doorsturen" — de CSV-import staat sinds #1322
/// op een eigen pagina, <see cref="TeambegeleidingImport"/>.
/// </summary>
public partial class Teambegeleiding : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private List<string> _teams = new();
    private List<TeambegeleidingItem> _begeleiders = new();
    private string? _selectedTeam;
    private bool _loadingTeams = true;
    private bool _loadingBegeleiders;
    private string? _teamsError;
    private bool _vraagFormulierZichtbaar;
    private DoorsturenRequest _doorsturen = new();
    private bool _doorsturenBezig;
    private string? _doorsturenError;
    private string? _doorsturenSuccess;
    private bool _gekopieerd;
    private string _ontvangers = "";

    // #1136: bewaakt dat een trage, verouderde teamlookup het resultaat van een snellere,
    // latere lookup niet meer kan overschrijven — zie LookupGeneratieGuard voor de volledige
    // uitleg van de race die hiermee wordt voorkomen.
    private readonly LookupGeneratieGuard _lookupGuard = new();
    private CancellationTokenSource? _lookupCts;

    private string OutlookRegel => string.Join("; ", _begeleiders
        .Where(b => !string.IsNullOrWhiteSpace(b.Emailadres))
        .GroupBy(b => (Naam: b.Naam.Trim(), Email: b.Emailadres!.Trim()), StringTupleComparer.OrdinalIgnoreCase)
        .Select(g => $"\"{g.Key.Naam}\" <{g.Key.Email}>"));

    private sealed class StringTupleComparer : IEqualityComparer<(string Naam, string Email)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();
        public bool Equals((string Naam, string Email) x, (string Naam, string Email) y) =>
            string.Equals(x.Naam, y.Naam, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Email, y.Email, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Naam, string Email) obj) =>
            HashCode.Combine(obj.Naam.ToUpperInvariant(), obj.Email.ToUpperInvariant());
    }

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override Task OnClubChangedAsync() => LoadAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lookupCts?.Cancel();
            _lookupCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task LoadAsync()
    {
        _loadingTeams = true;
        _teamsError = null;
        _selectedTeam = null;
        _begeleiders.Clear();
        var result = await Api.GetTeambegeleidingTeamsAsync();
        if (result.Success)
            _teams = result.Data ?? new();
        else
            _teamsError = result.ErrorMessage ?? "Ophalen teams mislukt";
        _loadingTeams = false;
    }

    private async Task OnTeamGekozen(ChangeEventArgs e)
    {
        _selectedTeam = e.Value?.ToString();
        _begeleiders.Clear();
        _ontvangers = ""; // reset direct — anders blijven de ontvangers van het vorige team
                           // zichtbaar totdat de nieuwe lookup is voltooid (#1136)
        _vraagFormulierZichtbaar = false;
        _doorsturenSuccess = null;
        _doorsturenError = null;
        _gekopieerd = false;

        var team = _selectedTeam;

        // #1136: elke selectie krijgt een eigen generatie-token. Voltooit een oudere lookup
        // (van een team dat inmiddels niet meer geselecteerd is) later dan een nieuwere, dan
        // wordt dat resultaat hieronder genegeerd in plaats van de huidige staat te overschrijven.
        var token = _lookupGuard.Start(team);

        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = null;

        if (string.IsNullOrEmpty(team))
        {
            _loadingBegeleiders = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _lookupCts = cts;

        _loadingBegeleiders = true;
        var result = await Api.GetTeambegeleidingAsync(team, cts.Token);

        if (!_lookupGuard.IsActueel(token))
            return; // Verouderde lookup — een nieuwere selectie is inmiddels gestart.

        if (result.Success)
            _begeleiders = result.Data ?? new();
        _loadingBegeleiders = false;
        HerstelOntvangers();
    }

    private void HerstelOntvangers() => _ontvangers = OutlookRegel;

    /// <summary>
    /// Niet-blokkerende waarschuwing (#765-review): een bewust ontbrekende allowlist mag een tikfout
    /// naar een verkeerd extern adres niet onopgemerkt laten. Grove substring-check op bekende
    /// begeleider-adressen — geen volledige e-mailparsing nodig voor een hint-only signaal.
    /// </summary>
    private List<string> OnbekendeAdressen()
    {
        var bekend = _begeleiders
            .Where(b => !string.IsNullOrWhiteSpace(b.Emailadres))
            .Select(b => b.Emailadres!.Trim())
            .ToList();

        return _ontvangers
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => f.Contains('@') && !bekend.Any(e => f.Contains(e, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private async Task KopieerNaarKlembordAsync(string tekst)
    {
        await JS.InvokeVoidAsync("blazorHelpers.copyToClipboard", tekst);
        _gekopieerd = true;
        StateHasChanged();
        await Task.Delay(2000);
        _gekopieerd = false;
        StateHasChanged();
    }

    private void ToonVraagFormulier()
    {
        _doorsturen = new DoorsturenRequest { TeamNaam = _selectedTeam ?? "" };
        _doorsturenError = null;
        _doorsturenSuccess = null;
        _vraagFormulierZichtbaar = true;
    }

    private void SluitVraagFormulier()
    {
        _vraagFormulierZichtbaar = false;
        _doorsturenError = null;
        _doorsturenSuccess = null;
    }

    private async Task VerstuurVraagAsync()
    {
        if (string.IsNullOrWhiteSpace(_doorsturen.Bericht))
        {
            _doorsturenError = "Vul een bericht in.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_ontvangers))
        {
            _doorsturenError = "Vul minimaal één ontvanger in bij \"Email Aan\".";
            return;
        }

        // #1136: team + ontvangers atomisch vastleggen vóór de await. De teamkiezer blijft
        // tijdens het versturen bruikbaar; zonder deze snapshot zou een teamwissel tijdens de
        // openstaande aanroep de payload (of in elk geval de succesmelding) alsnog kunnen laten
        // verwijzen naar het nieuw geselecteerde team in plaats van het team waarvoor is verstuurd.
        var teamNaamSnapshot = _selectedTeam ?? "";
        var ontvangersSnapshot = _ontvangers;

        _doorsturenBezig = true;
        _doorsturenError = null;
        _doorsturen.TeamNaam = teamNaamSnapshot;
        _doorsturen.Ontvangers = ontvangersSnapshot;

        var result = await Api.StuurTeambegeleidingBerichtAsync(_doorsturen);
        _doorsturenBezig = false;

        if (result.Success)
        {
            _doorsturenSuccess = $"Uw vraag over de begeleiding van {teamNaamSnapshot} is doorgestuurd. De begeleider neemt rechtstreeks contact met u op.";
            _vraagFormulierZichtbaar = false;
        }
        else
        {
            _doorsturenError = result.ErrorMessage ?? "Doorsturen mislukt. Probeer opnieuw.";
        }
    }
}
