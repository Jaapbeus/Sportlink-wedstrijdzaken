using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Shared;

public partial class TeamKoppelFormulier
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    /// <summary>Voorgevulde teamtekst (bijv. uit de trace of de wachtrij).</summary>
    [Parameter] public string RuweTekst { get; set; } = "";

    /// <summary><c>true</c> als de tekst niet aanpasbaar is (de trace toont precies de tekst die niet herkend werd).</summary>
    [Parameter] public bool TekstVergrendeld { get; set; }

    /// <summary>Verwerking waaruit deze tekst komt; wordt als aanwijzing bij de alias bewaard.</summary>
    [Parameter] public int? HerkomstVerwerkingId { get; set; }

    [Parameter] public EventCallback<TeamAliasAanmaakResultaatDto> OnGekoppeld { get; set; }
    [Parameter] public EventCallback OnAnnuleer { get; set; }

    private readonly string _tekstId = $"koppel-tekst-{Guid.NewGuid():N}";
    private readonly string _teamId = $"koppel-team-{Guid.NewGuid():N}";
    private List<TeamKeuzeDto> _teams = new();
    private string _tekst = "";
    private int _gekozenTeamId;
    private bool _bezig;
    private string? _fout;
    private string? _conflict;
    private string? _dubbelzinnig;
    private string? _teamsFout;

    private bool KanOpslaan => !_bezig && _gekozenTeamId > 0 && !string.IsNullOrWhiteSpace(_tekst);

    protected override async Task OnInitializedAsync()
    {
        _tekst = RuweTekst;
        var r = await Api.GetTeamKeuzelijstAsync();
        if (r.Success && r.Data is { Count: > 0 }) _teams = r.Data;
        else _teamsFout = r.Success
            ? "Er zijn geen teams bekend. Bouw eerst de teamlijst op (knop op het scherm Teamaliassen)."
            : r.ErrorMessage ?? "Teams ophalen mislukt";
    }

    private async Task OpslaanAsync(bool herkoppel, bool bevestigDubbelzinnig = false)
    {
        _bezig = true;
        _fout = null;
        _conflict = null;
        _dubbelzinnig = null;
        var r = await Api.MaakTeamAliasAsync(new TeamAliasAanmaakDto
        {
            RuweTekst = _tekst.Trim(),
            TeamId = _gekozenTeamId,
            Herkoppel = herkoppel,
            BevestigDubbelzinnig = bevestigDubbelzinnig,
            HerkomstVerwerkingId = HerkomstVerwerkingId
        });
        _bezig = false;

        if (r.Success && r.Data is not null) await OnGekoppeld.InvokeAsync(r.Data);
        else if (r.StatusCode == 409 && r.Code == "dubbelzinnig") _dubbelzinnig = r.ErrorMessage;
        else if (r.StatusCode == 409) _conflict = r.ErrorMessage;
        else _fout = r.ErrorMessage ?? "Koppelen mislukt";
    }

    private Task AnnuleerAsync() => OnAnnuleer.InvokeAsync();
}
