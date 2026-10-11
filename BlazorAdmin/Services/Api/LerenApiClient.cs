using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Leermomenten, teamaliassen en de wachtrij met onbekende teamteksten (#323, #701, #1568). HTTP, status- en foutafhandeling zit in <see cref="AdminApiTransport"/> (#1630).</summary>
public sealed class LerenApiClient
{
    private readonly AdminApiTransport _t;

    public LerenApiClient(AdminApiTransport transport) => _t = transport;

    // ── Leermomenten (#323) ──

    public async Task<ApiResult<LeermomentenResponse>> GetLeermomentenAsync(string? status = null, int limit = 50)
    {
        var path = $"api/beheer/leermomenten?limit={limit}";
        if (!string.IsNullOrWhiteSpace(status)) path += $"&status={Uri.EscapeDataString(status)}";
        return await _t.GetAsync<LeermomentenResponse>(path);
    }

    public async Task<ApiResult<LeermomentenStatsDto>> GetLeermomentenStatsAsync()
        => await _t.GetAsync<LeermomentenStatsDto>("api/beheer/leermomenten/stats");

    public async Task<ApiResult<object>> ValideerLeermomentAsync(int id, string actie)
        => await _t.PutAsync<object>($"api/beheer/leermomenten/{id}/valideer", new { actie });

    // ── Teamaliassen (#701) ──

    public async Task<ApiResult<TeamAliassenResponse>> GetTeamAliassenAsync(string? status = null, int limit = 100)
    {
        var path = $"api/beheer/teamaliassen?limit={limit}";
        if (!string.IsNullOrWhiteSpace(status)) path += $"&status={Uri.EscapeDataString(status)}";
        return await _t.GetAsync<TeamAliassenResponse>(path);
    }

    /// <summary>status: "validated" (goedkeuren) of "rejected" (afwijzen).</summary>
    public async Task<ApiResult<object>> ValideerTeamAliasAsync(int id, string status)
        => await _t.PutAsync<object>($"api/beheer/teamaliassen/{id}/valideer", new { status });

    public async Task<ApiResult<object>> DeleteTeamAliasAsync(int id)
        => await _t.DeleteAsync<object>($"api/beheer/teamaliassen/{id}");

    /// <summary>Leermoment door een beheerder (#1568 deel C): direct gevalideerd, permanent.</summary>
    public async Task<ApiResult<object>> MaakLeermomentAsync(LeermomentAanmaakDto dto)
        => await _t.PostAsync<object>("api/beheer/leermomenten", dto);

    /// <summary>Verwijdert een door een beheerder toegevoegd leermoment (AVG); een leermoment uit een beantwoorde mail geeft 409.</summary>
    public async Task<ApiResult<object>> VerwijderLeermomentAsync(int id)
        => await _t.DeleteAsync<object>($"api/beheer/leermomenten/{id}");

    public async Task<ApiResult<List<TeamKeuzeDto>>> GetTeamKeuzelijstAsync()
        => await _t.GetAsync<List<TeamKeuzeDto>>("api/beheer/teams/keuzelijst");

    /// <summary>Alias aanmaken door een beheerder (#1568 deel C). Een 409 betekent: de schrijfwijze hoort al bij een ander team.</summary>
    public async Task<ApiResult<TeamAliasAanmaakResultaatDto>> MaakTeamAliasAsync(TeamAliasAanmaakDto dto)
        => await _t.PostAsync<TeamAliasAanmaakResultaatDto>("api/beheer/teamaliassen", dto);

    // ── Wachtrij onbekende teamteksten (#1568 deel C) ──

    public async Task<ApiResult<OnbekendeTeamTekstenResponse>> GetOnbekendeTeamTekstenAsync(string? status = "open", int limit = 100)
        => await _t.GetAsync<OnbekendeTeamTekstenResponse>(
            $"api/beheer/onbekende-teamteksten?limit={limit}" + (string.IsNullOrEmpty(status) ? "" : $"&status={Uri.EscapeDataString(status)}"));

    public async Task<ApiResult<object>> ZetOnbekendeTeamTekstStatusAsync(int id, string status)
        => await _t.PutAsync<object>($"api/beheer/onbekende-teamteksten/{id}/status", new { status });
}
