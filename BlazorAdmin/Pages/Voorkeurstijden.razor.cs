using BlazorAdmin.Models;
using BlazorAdmin.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorAdmin.Pages;

public partial class Voorkeurstijden : ClubSelectorPageBase
{
    [Inject] private AdminApiClient Api { get; set; } = default!;

    private List<VoorkeurTijdDto> items = new();
    private List<TeamRegelDto> teamRegels = new();
    private List<string> teams = new();
    private VoorkeurTijdDto? editing;
    private TeamRegelDto? editingRegel;
    private bool isNew;
    private bool isNewRegel;
    private bool loading = true;
    private string filterTeam = "";
    private string? message;
    private string? regelMessage;

    private List<VoorkeurTijdDto> Gefilterd =>
        string.IsNullOrWhiteSpace(filterTeam)
            ? items
            : items.Where(i => i.TeamNaam.Contains(filterTeam, StringComparison.OrdinalIgnoreCase)).ToList();

    // #288: alleen actieve teamregels tonen in de tabel
    private List<TeamRegelDto> ActieveTeamRegels => teamRegels.Where(r => r.Actief).ToList();

    protected override async Task OnInitializedAsync() => await LoadAsync();

    protected override Task OnClubChangedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        loading = true;
        var teamsResult = await Api.GetTeamsAsync();
        teams = teamsResult.Success ? teamsResult.Data ?? new() : new();
        var r = await Api.GetVoorkeurTijdenAsync();
        items = r.Success ? r.Data ?? new() : new();
        var regelsResult = await Api.GetTeamRegelsAsync();
        teamRegels = regelsResult.Success ? regelsResult.Data ?? new() : new();
        loading = false;
    }

    private void Edit(VoorkeurTijdDto v)
    {
        editing = new VoorkeurTijdDto
        {
            Id = v.Id,
            TeamNaam = v.TeamNaam,
            DagVanWeek = v.DagVanWeek,
            VoorkeurTijd = v.VoorkeurTijd,
            Prioriteit = v.Prioriteit,
            Actief = v.Actief,
            ClubCode = v.ClubCode
        };
        isNew = false;
    }

    private void StartNew()
    {
        editing = new VoorkeurTijdDto { DagVanWeek = 6, VoorkeurTijd = "10:00", Prioriteit = 5, Actief = true };
        isNew = true;
    }

    private async Task OpslaanAsync()
    {
        if (editing == null) return;
        var r = isNew
            ? await Api.CreateVoorkeurTijdAsync(editing)
            : await Api.UpdateVoorkeurTijdAsync(editing.Id, editing);
        message = r.Success ? "Opgeslagen." : "Fout: " + r.ErrorMessage;
        if (r.Success) { editing = null; await LoadAsync(); }
    }

    private async Task DeleteAsync(int id)
    {
        var r = await Api.DeleteVoorkeurTijdAsync(id);
        message = r.Success ? "Soft-deleted." : "Fout: " + r.ErrorMessage;
        await LoadAsync();
    }

    private void EditRegel(TeamRegelDto r)
    {
        editingRegel = new TeamRegelDto
        {
            Id = r.Id, TeamNaam = r.TeamNaam, RegelType = r.RegelType,
            WaardeMinuten = r.WaardeMinuten, WaardeVeldNummer = r.WaardeVeldNummer,
            WaardeTijd = r.WaardeTijd, Prioriteit = r.Prioriteit,
            Actief = r.Actief, Opmerking = r.Opmerking, ClubCode = r.ClubCode
        };
        isNewRegel = false;
        regelMessage = null;
    }

    private void StartNewRegel()
    {
        editingRegel = new TeamRegelDto { RegelType = "BufferVoor", Actief = true };
        isNewRegel = true;
        regelMessage = null;
    }

    private async Task OpslaanRegelAsync()
    {
        if (editingRegel == null) return;
        var r = isNewRegel
            ? await Api.CreateTeamRegelAsync(editingRegel)
            : await Api.UpdateTeamRegelAsync(editingRegel.Id, editingRegel);
        regelMessage = r.Success ? "Opgeslagen." : "Fout: " + r.ErrorMessage;
        if (r.Success) { editingRegel = null; await LoadAsync(); }
    }

    private async Task DeleteRegelAsync(int id)
    {
        var r = await Api.DeleteTeamRegelAsync(id);
        regelMessage = r.Success ? "Verwijderd." : "Fout: " + r.ErrorMessage;
        await LoadAsync();
    }

    private static string RegelTypeNederlands(string? type) => type switch
    {
        "BufferVoor" => "Buffer vóór",
        "BufferNa" => "Buffer na",
        "VoorkeurVeld" => "Voorkeursveld",
        _ => type ?? ""
    };

    private static string RegelWaarde(TeamRegelDto r) => r.RegelType switch
    {
        "BufferVoor" or "BufferNa" => r.WaardeMinuten.HasValue ? $"{r.WaardeMinuten} min" : "—",
        "VoorkeurVeld" => r.WaardeVeldNummer.HasValue
            ? (string.IsNullOrWhiteSpace(r.WaardeTijd) ? $"veld {r.WaardeVeldNummer}" : $"veld {r.WaardeVeldNummer} om {r.WaardeTijd}")
            : "—",
        _ => "—"
    };
}
