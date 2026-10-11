using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Instellingen, toegangsmatrix, sync, clubs, thema, geocoding en ALLSTARS-testdata. HTTP, status- en foutafhandeling zit in <see cref="AdminApiTransport"/> (#1630).</summary>
public sealed class InstellingenApiClient
{
    private readonly AdminApiTransport _t;

    public InstellingenApiClient(AdminApiTransport transport) => _t = transport;

    // ── Settings ──

    public async Task<ApiResult<AppSettingsDto>> GetSettingsAsync()
        => await _t.GetAsync<AppSettingsDto>("api/beheer/settings");

    public async Task<ApiResult<SettingsUpdateResultDto>> UpdateSettingsAsync(SettingsUpdateDto dto)
        => await _t.PutAsync<SettingsUpdateResultDto>("api/beheer/settings", dto);

    // ── Per-club, per-rol toegangsmatrix (#1390, opvolger van #1341/epic #1338) ──

    public async Task<ApiResult<List<RolFeatureInstellingDto>>> GetRolFeatureInstellingenAsync()
        => await _t.GetAsync<List<RolFeatureInstellingDto>>("api/beheer/rolfeatureinstellingen");

    public async Task<ApiResult<object>> ZetRolFeatureInstellingAsync(string rolNaam, string featureKey, bool enabled)
        => await _t.PutAsync<object>("api/beheer/rolfeatureinstellingen",
            new { RolNaam = rolNaam, FeatureKey = featureKey, Enabled = enabled });

    // ── Sync ──

    public async Task<ApiResult<SyncStatusDto>> GetSyncStatusAsync(Guid? jobId = null)
        => await _t.GetAsync<SyncStatusDto>(jobId.HasValue
            ? $"api/beheer/sync/status?jobId={jobId}"
            : "api/beheer/sync/status");

    /// <summary>
    /// Start een sync. Zonder argumenten het standaardvenster; met <paramref name="seizoenStartjaar"/>
    /// een volledige herberekening vanaf dat seizoen (#1352 — body <c>{reset:true, season}</c>).
    /// </summary>
    public async Task<ApiResult<TriggerSyncResultDto>> TriggerSyncAsync(int? seizoenStartjaar = null)
        => await _t.PostAsync<TriggerSyncResultDto>("api/beheer/sync/trigger",
            seizoenStartjaar is int jaar ? new { reset = true, season = jaar } : new { });

    // ── Geocoding ──

    public async Task<ApiResult<GeocodeResultDto>> GeocodeAsync(string plaatsnaam)
        => await _t.GetAsync<GeocodeResultDto>($"api/beheer/geocode?plaatsnaam={Uri.EscapeDataString(plaatsnaam)}");

    // ── Clubs (#324) ──

    public async Task<ApiResult<List<ClubDto>>> GetClubsAsync()
        => await _t.GetAsync<List<ClubDto>>("api/beheer/clubs");

    // ── Thema (#325) ──

    public async Task<ApiResult<ThemeDto>> GetThemeAsync()
        => await _t.GetAsync<ThemeDto>("api/beheer/theme");

    public async Task<ApiResult<object>> UpdateThemeAsync(ThemeDto dto)
        => await _t.PutAsync<object>("api/beheer/theme", dto);

    public async Task<ApiResult<ThemeExtractResultDto>> ExtractThemeColorsAsync(string url)
        => await _t.PostAsync<ThemeExtractResultDto>($"api/beheer/theme/extract?url={Uri.EscapeDataString(url)}", new { });

    // ── Test data / ALLSTARS (#365) ──

    public async Task<ApiResult<List<AllstarsWedstrijdDto>>> GetAllstarsWedstrijdenAsync()
        => await _t.GetAsync<List<AllstarsWedstrijdDto>>("api/beheer/testdata/wedstrijden");
    public async Task<ApiResult<List<string>>> GetAllstarsTeamsAsync()
        => await _t.GetAsync<List<string>>("api/beheer/testdata/teams");
    public async Task<ApiResult<object>> UpsertAllstarsWedstrijdAsync(AllstarsWedstrijdDto dto)
        => await _t.PostAsync<object>("api/beheer/testdata/wedstrijden", dto);
    public async Task<ApiResult<object>> DeleteAllstarsWedstrijdAsync(string bk)
        => await _t.DeleteAsync<object>($"api/beheer/testdata/wedstrijden/{Uri.EscapeDataString(bk)}");
    public async Task<ApiResult<object>> DeleteAlleAllstarsWedstrijdenAsync(string? van, string? tot)
    {
        var q = new List<string>();
        if (!string.IsNullOrEmpty(van)) q.Add($"van={Uri.EscapeDataString(van)}");
        if (!string.IsNullOrEmpty(tot)) q.Add($"tot={Uri.EscapeDataString(tot)}");
        var qs = q.Count > 0 ? "?" + string.Join("&", q) : "";
        return await _t.DeleteAsync<object>($"api/beheer/testdata/wedstrijden{qs}");
    }
    public async Task<ApiResult<AllstarsVerplaatsDatumResultaat>> VerplaatsAllstarsDatumAsync(string oudeDatum, string nieuweDatum)
        => await _t.PostAsync<AllstarsVerplaatsDatumResultaat>("api/beheer/testdata/wedstrijden/verplaats-datum",
               new { oudeDatum, nieuweDatum });
}
