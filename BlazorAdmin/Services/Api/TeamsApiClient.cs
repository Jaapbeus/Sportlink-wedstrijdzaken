using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Teams, voorkeurstijden, teamregels, teambegeleiding en speeltijden. HTTP, status- en foutafhandeling zit in <see cref="AdminApiTransport"/> (#1630).</summary>
public sealed class TeamsApiClient
{
    private readonly AdminApiTransport _t;

    public TeamsApiClient(AdminApiTransport transport) => _t = transport;

    // ── Teams ──

    public async Task<ApiResult<List<string>>> GetTeamsAsync()
        => await _t.GetAsync<List<string>>("api/beheer/teams");

    /// <summary>
    /// Bouwt de canonieke teamlijst opnieuw op uit de gesynchroniseerde teams (#946). Geeft 409
    /// terug wanneer er nog niets gesynchroniseerd is — dan valt er niets af te leiden.
    /// </summary>
    public async Task<ApiResult<TeamHerstelDto>> HerstelTeamlijstAsync()
        => await _t.PostAsync<TeamHerstelDto>("api/beheer/teams/herstel", new { });

    // ── Voorkeurstijden ──

    public async Task<ApiResult<List<VoorkeurTijdDto>>> GetVoorkeurTijdenAsync(string? team = null)
    {
        var path = "api/beheer/voorkeurstijden";
        if (!string.IsNullOrWhiteSpace(team))
            path += "?team=" + Uri.EscapeDataString(team);
        return await _t.GetAsync<List<VoorkeurTijdDto>>(path);
    }

    public async Task<ApiResult<object>> CreateVoorkeurTijdAsync(VoorkeurTijdDto dto)
        => await _t.PostAsync<object>("api/beheer/voorkeurstijden", dto);

    public async Task<ApiResult<object>> UpdateVoorkeurTijdAsync(int id, VoorkeurTijdDto dto)
        => await _t.PutAsync<object>($"api/beheer/voorkeurstijden/{id}", dto);

    public async Task<ApiResult<object>> DeleteVoorkeurTijdAsync(int id)
        => await _t.DeleteAsync<object>($"api/beheer/voorkeurstijden/{id}");

    // ── Teamregels ──

    public async Task<ApiResult<List<TeamRegelDto>>> GetTeamRegelsAsync()
        => await _t.GetAsync<List<TeamRegelDto>>("api/beheer/teamregels");

    public async Task<ApiResult<object>> CreateTeamRegelAsync(TeamRegelDto dto)
        => await _t.PostAsync<object>("api/beheer/teamregels", dto);

    public async Task<ApiResult<object>> UpdateTeamRegelAsync(int id, TeamRegelDto dto)
        => await _t.PutAsync<object>($"api/beheer/teamregels/{id}", dto);

    public async Task<ApiResult<object>> DeleteTeamRegelAsync(int id)
        => await _t.DeleteAsync<object>($"api/beheer/teamregels/{id}");

    // ── Teambegeleiding ──

    public async Task<ApiResult<List<string>>> GetTeambegeleidingTeamsAsync()
        => await _t.GetAsync<List<string>>("api/beheer/teambegeleiding");

    // cancellationToken (#1136): laat de aanroeper een verouderde lookup (team-selectie
    // inmiddels gewijzigd) daadwerkelijk annuleren in plaats van het resultaat alleen weg te
    // gooien — de generatie-guard in Teambegeleiding.razor blijft de correctheidsgarantie,
    // dit is puur een netwerk-optimalisatie.
    public async Task<ApiResult<List<TeambegeleidingItem>>> GetTeambegeleidingAsync(
        string team, CancellationToken cancellationToken = default)
        => await _t.GetAsync<List<TeambegeleidingItem>>(
            $"api/beheer/teambegeleiding/{Uri.EscapeDataString(team)}", cancellationToken);

    public async Task<ApiResult<object>> StuurTeambegeleidingBerichtAsync(DoorsturenRequest request)
        => await _t.PostAsync<object>("api/beheer/teambegeleiding/doorsturen", request);

    public async Task<ApiResult<TeambegeldingImportResultaat>> ImporteerTeambegeleidingAsync(
        string csvContent, string? bestandsnaam)
        => await _t.PostAsync<TeambegeldingImportResultaat>("api/beheer/teambegeleiding/import",
            new TeambegeldingImportRequest { CsvContent = csvContent, Bestandsnaam = bestandsnaam });

    // ── Speeltijden ──

    public async Task<ApiResult<List<SpeeltijdDto>>> GetSpeeltijdenAsync()
        => await _t.GetAsync<List<SpeeltijdDto>>("api/beheer/speeltijden");

    public async Task<ApiResult<object>> CreateSpeeltijdAsync(SpeeltijdDto dto)
        => await _t.PostAsync<object>("api/beheer/speeltijden", dto);

    public async Task<ApiResult<object>> UpdateSpeeltijdAsync(string leeftijd, SpeeltijdDto dto)
        => await _t.PutAsync<object>($"api/beheer/speeltijden/{Uri.EscapeDataString(leeftijd)}", dto);

    public async Task<ApiResult<object>> DeleteSpeeltijdAsync(string leeftijd)
        => await _t.DeleteAsync<object>($"api/beheer/speeltijden/{Uri.EscapeDataString(leeftijd)}");
}
