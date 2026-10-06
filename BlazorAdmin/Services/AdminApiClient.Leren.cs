using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Leeracties vanuit de trace (#1568 deel C): alias aanmaken, leermoment toevoegen en de wachtrij met onbekende
/// teamteksten. Apart bestand zodat <c>AdminApiClient.cs</c> onder de 500 regels blijft.
/// </summary>
public partial class AdminApiClient
{
    /// <summary>Leermoment door een beheerder (#1568 deel C): direct gevalideerd, permanent.</summary>
    public async Task<ApiResult<object>> MaakLeermomentAsync(LeermomentAanmaakDto dto)
        => await PostAsync<object>("api/beheer/leermomenten", dto);

    /// <summary>Verwijdert een door een beheerder toegevoegd leermoment (AVG); een leermoment uit een beantwoorde mail geeft 409.</summary>
    public async Task<ApiResult<object>> VerwijderLeermomentAsync(int id)
        => await DeleteAsync<object>($"api/beheer/leermomenten/{id}");

    public async Task<ApiResult<List<TeamKeuzeDto>>> GetTeamKeuzelijstAsync()
        => await GetAsync<List<TeamKeuzeDto>>("api/beheer/teams/keuzelijst");

    /// <summary>Alias aanmaken door een beheerder (#1568 deel C). Een 409 betekent: de schrijfwijze hoort al bij een ander team.</summary>
    public async Task<ApiResult<TeamAliasAanmaakResultaatDto>> MaakTeamAliasAsync(TeamAliasAanmaakDto dto)
        => await PostAsync<TeamAliasAanmaakResultaatDto>("api/beheer/teamaliassen", dto);

    // ── Wachtrij onbekende teamteksten (#1568 deel C) ──

    public async Task<ApiResult<OnbekendeTeamTekstenResponse>> GetOnbekendeTeamTekstenAsync(string? status = "open", int limit = 100)
        => await GetAsync<OnbekendeTeamTekstenResponse>(
            $"api/beheer/onbekende-teamteksten?limit={limit}" + (string.IsNullOrEmpty(status) ? "" : $"&status={Uri.EscapeDataString(status)}"));

    public async Task<ApiResult<object>> ZetOnbekendeTeamTekstStatusAsync(int id, string status)
        => await PutAsync<object>($"api/beheer/onbekende-teamteksten/{id}/status", new { status });
}
