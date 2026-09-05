using System.Net.Http.Json;
using System.Text.Json;
using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Wrapper rondom HttpClient voor alle Admin API calls.
/// BaseUrl komt uit appsettings.json (FunctionBaseUrl).
/// In productie (SWA) is FunctionBaseUrl leeg → relatieve URLs via SWA proxying.
/// </summary>
public class AdminApiClient
{
    private readonly HttpClient _http;
    private readonly ApiStatusService _status;
    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);

    public AdminApiClient(HttpClient http, ApiStatusService status)
    {
        _http = http;
        _status = status;
    }

    // ── Settings ──

    public async Task<ApiResult<AppSettingsDto>> GetSettingsAsync()
        => await GetAsync<AppSettingsDto>("api/beheer/settings");

    public async Task<ApiResult<SettingsUpdateResultDto>> UpdateSettingsAsync(SettingsUpdateDto dto)
        => await PutAsync<SettingsUpdateResultDto>("api/beheer/settings", dto);

    // ── Sportlink Web Extension — rol↔serviceaccount-koppelingsstatus (#988) ──

    public async Task<ApiResult<List<SportlinkExtensieRolDto>>> GetSportlinkExtensieRollenAsync()
        => await GetAsync<List<SportlinkExtensieRolDto>>("api/beheer/sportlink-extensie/rollen");

    public async Task<ApiResult<object>> RegistreerSportlinkKoppelingAsync(string rolNaam, string? sportlinkAccountNaam)
        => await PutAsync<object>($"api/beheer/sportlink-extensie/rollen/{Uri.EscapeDataString(rolNaam)}",
            new { SportlinkAccountNaam = sportlinkAccountNaam });

    // #991: write-only bootstrap van het échte refresh-token — nooit een GET-tegenhanger.
    public async Task<ApiResult<object>> RegistreerSportlinkTokenAsync(string rolNaam, string refreshToken)
        => await PutAsync<object>($"api/beheer/sportlink-extensie/rollen/{Uri.EscapeDataString(rolNaam)}/token",
            new { RefreshToken = refreshToken });

    // #991: read-only Sportlink-paneel per wedstrijd in Dagplanning.
    public async Task<ApiResult<SportlinkMatchInfoDto>> GetSportlinkMatchInfoAsync(string wedstrijdcode)
        => await GetAsync<SportlinkMatchInfoDto>($"api/sportlink/match/{Uri.EscapeDataString(wedstrijdcode)}");

    // #989: lichtgewicht variant voor de deep-link-knop — alleen PublicMatchId, geen volledige Match-aanroep.
    public async Task<ApiResult<SportlinkPublicMatchIdDto>> GetSportlinkPublicMatchIdAsync(string wedstrijdcode)
        => await GetAsync<SportlinkPublicMatchIdDto>($"api/sportlink/match/{Uri.EscapeDataString(wedstrijdcode)}/public-match-id");

    // ── Sync ──

    public async Task<ApiResult<SyncStatusDto>> GetSyncStatusAsync()
        => await GetAsync<SyncStatusDto>("api/beheer/sync/status");

    public async Task<ApiResult<object>> TriggerSyncAsync()
        => await PostAsync<object>("api/beheer/sync/trigger", new { });

    // ── Templates ──

    public async Task<ApiResult<List<TemplateDto>>> GetTemplatesAsync()
        => await GetAsync<List<TemplateDto>>("api/beheer/templates");

    public async Task<ApiResult<object>> UpdateTemplateAsync(string key, TemplateDto dto)
        => await PutAsync<object>($"api/beheer/templates/{Uri.EscapeDataString(key)}", dto);

    public async Task<ApiResult<object>> ResetTemplateAsync(string key)
        => await PostAsync<object>($"api/beheer/templates/{Uri.EscapeDataString(key)}/reset", new { });

    // ── Teams ──

    public async Task<ApiResult<List<string>>> GetTeamsAsync()
        => await GetAsync<List<string>>("api/beheer/teams");

    /// <summary>
    /// Bouwt de canonieke teamlijst opnieuw op uit de gesynchroniseerde teams (#946). Geeft 409
    /// terug wanneer er nog niets gesynchroniseerd is — dan valt er niets af te leiden.
    /// </summary>
    public async Task<ApiResult<TeamHerstelDto>> HerstelTeamlijstAsync()
        => await PostAsync<TeamHerstelDto>("api/beheer/teams/herstel", new { });

    // ── Voorkeurstijden ──

    public async Task<ApiResult<List<VoorkeurTijdDto>>> GetVoorkeurTijdenAsync(string? team = null)
    {
        var path = "api/beheer/voorkeurstijden";
        if (!string.IsNullOrWhiteSpace(team))
            path += "?team=" + Uri.EscapeDataString(team);
        return await GetAsync<List<VoorkeurTijdDto>>(path);
    }

    public async Task<ApiResult<object>> CreateVoorkeurTijdAsync(VoorkeurTijdDto dto)
        => await PostAsync<object>("api/beheer/voorkeurstijden", dto);

    public async Task<ApiResult<object>> UpdateVoorkeurTijdAsync(int id, VoorkeurTijdDto dto)
        => await PutAsync<object>($"api/beheer/voorkeurstijden/{id}", dto);

    public async Task<ApiResult<object>> DeleteVoorkeurTijdAsync(int id)
        => await DeleteAsync<object>($"api/beheer/voorkeurstijden/{id}");

    // ── Teamregels ──

    public async Task<ApiResult<List<TeamRegelDto>>> GetTeamRegelsAsync()
        => await GetAsync<List<TeamRegelDto>>("api/beheer/teamregels");

    public async Task<ApiResult<object>> CreateTeamRegelAsync(TeamRegelDto dto)
        => await PostAsync<object>("api/beheer/teamregels", dto);

    public async Task<ApiResult<object>> UpdateTeamRegelAsync(int id, TeamRegelDto dto)
        => await PutAsync<object>($"api/beheer/teamregels/{id}", dto);

    public async Task<ApiResult<object>> DeleteTeamRegelAsync(int id)
        => await DeleteAsync<object>($"api/beheer/teamregels/{id}");

    // ── Uitgesloten e-mailadressen ──

    public async Task<ApiResult<List<UitgeslotenEmailAdresDto>>> GetUitgeslotenEmailsAsync()
        => await GetAsync<List<UitgeslotenEmailAdresDto>>("api/beheer/uitgesloten-emails");

    public async Task<ApiResult<object>> CreateUitgeslotenEmailAsync(UitgeslotenEmailAdresDto dto)
        => await PostAsync<object>("api/beheer/uitgesloten-emails", dto);

    public async Task<ApiResult<object>> DeleteUitgeslotenEmailAsync(int id)
        => await DeleteAsync<object>($"api/beheer/uitgesloten-emails/{id}");

    // ── Email log ──

    public async Task<ApiResult<EmailLogResponse>> GetEmailLogAsync(
        DateTime? vanaf = null, DateTime? tot = null, string? status = null, int limit = 50)
    {
        var qp = new List<string> { $"limit={limit}" };
        if (vanaf.HasValue) qp.Add($"vanaf={vanaf:yyyy-MM-dd}");
        if (tot.HasValue) qp.Add($"tot={tot:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(status)) qp.Add($"status={Uri.EscapeDataString(status)}");
        return await GetAsync<EmailLogResponse>("api/beheer/email-log?" + string.Join("&", qp));
    }

    // ── Geocoding ──

    public async Task<ApiResult<GeocodeResultDto>> GeocodeAsync(string plaatsnaam)
        => await GetAsync<GeocodeResultDto>($"api/beheer/geocode?plaatsnaam={Uri.EscapeDataString(plaatsnaam)}");

    // ── Email tester ──

    public async Task<ApiResult<TestEmailResponse>> TestEmailAsync(TestEmailRequest dto)
        => await PostAsync<TestEmailResponse>("api/test/email", dto);

    // ── Auto-plan (#380) — de enige dagplanning-optimalisatie sinds #666 ──

    public async Task<ApiResult<AutoPlanResponseDto>> AutoPlanAsync(AutoPlanRequestDto req)
        => await PostAsync<AutoPlanResponseDto>("api/planner/auto-plan", req);

    public async Task<ApiResult<AutoPlanToepassenResponseDto>> AutoPlanToepassenAsync(AutoPlanToepassenRequestDto req)
        => await PostAsync<AutoPlanToepassenResponseDto>("api/planner/auto-plan/toepassen", req);

    public async Task<ApiResult<List<VeldbezettingItemDto>>> GetVeldbezettingAsync(string datum)
        => await GetAsync<List<VeldbezettingItemDto>>($"api/planner/veldbezetting?datum={Uri.EscapeDataString(datum)}");

    // ── Teambegeleiding ──

    public async Task<ApiResult<List<string>>> GetTeambegeleidingTeamsAsync()
        => await GetAsync<List<string>>("api/beheer/teambegeleiding");

    public async Task<ApiResult<List<TeambegeleidingItem>>> GetTeambegeleidingAsync(string team)
        => await GetAsync<List<TeambegeleidingItem>>($"api/beheer/teambegeleiding/{Uri.EscapeDataString(team)}");

    public async Task<ApiResult<object>> StuurTeambegeleidingBerichtAsync(DoorsturenRequest request)
        => await PostAsync<object>("api/beheer/teambegeleiding/doorsturen", request);

    public async Task<ApiResult<TeambegeldingImportResultaat>> ImporteerTeambegeleidingAsync(
        string csvContent, string? bestandsnaam)
        => await PostAsync<TeambegeldingImportResultaat>("api/beheer/teambegeleiding/import",
            new TeambegeldingImportRequest { CsvContent = csvContent, Bestandsnaam = bestandsnaam });

    // ── Speeltijden ──

    public async Task<ApiResult<List<SpeeltijdDto>>> GetSpeeltijdenAsync()
        => await GetAsync<List<SpeeltijdDto>>("api/beheer/speeltijden");

    public async Task<ApiResult<object>> CreateSpeeltijdAsync(SpeeltijdDto dto)
        => await PostAsync<object>("api/beheer/speeltijden", dto);

    public async Task<ApiResult<object>> UpdateSpeeltijdAsync(string leeftijd, SpeeltijdDto dto)
        => await PutAsync<object>($"api/beheer/speeltijden/{Uri.EscapeDataString(leeftijd)}", dto);

    public async Task<ApiResult<object>> DeleteSpeeltijdAsync(string leeftijd)
        => await DeleteAsync<object>($"api/beheer/speeltijden/{Uri.EscapeDataString(leeftijd)}");

    // ── Leermomenten (#323) ──

    public async Task<ApiResult<LeermomentenResponse>> GetLeermomentenAsync(string? status = null, int limit = 50)
    {
        var path = $"api/beheer/leermomenten?limit={limit}";
        if (!string.IsNullOrWhiteSpace(status)) path += $"&status={Uri.EscapeDataString(status)}";
        return await GetAsync<LeermomentenResponse>(path);
    }

    public async Task<ApiResult<LeermomentenStatsDto>> GetLeermomentenStatsAsync()
        => await GetAsync<LeermomentenStatsDto>("api/beheer/leermomenten/stats");

    public async Task<ApiResult<object>> ValideerLeermomentAsync(int id, string actie)
        => await PutAsync<object>($"api/beheer/leermomenten/{id}/valideer", new { actie });

    // ── Teamaliassen (#701) ──

    public async Task<ApiResult<TeamAliassenResponse>> GetTeamAliassenAsync(string? status = null, int limit = 100)
    {
        var path = $"api/beheer/teamaliassen?limit={limit}";
        if (!string.IsNullOrWhiteSpace(status)) path += $"&status={Uri.EscapeDataString(status)}";
        return await GetAsync<TeamAliassenResponse>(path);
    }

    /// <summary>status: "validated" (goedkeuren) of "rejected" (afwijzen).</summary>
    public async Task<ApiResult<object>> ValideerTeamAliasAsync(int id, string status)
        => await PutAsync<object>($"api/beheer/teamaliassen/{id}/valideer", new { status });

    public async Task<ApiResult<object>> DeleteTeamAliasAsync(int id)
        => await DeleteAsync<object>($"api/beheer/teamaliassen/{id}");

    // ── Clubs (#324) ──

    public async Task<ApiResult<List<ClubDto>>> GetClubsAsync()
        => await GetAsync<List<ClubDto>>("api/beheer/clubs");

    // ── Thema (#325) ──

    public async Task<ApiResult<ThemeDto>> GetThemeAsync()
        => await GetAsync<ThemeDto>("api/beheer/theme");

    public async Task<ApiResult<object>> UpdateThemeAsync(ThemeDto dto)
        => await PutAsync<object>("api/beheer/theme", dto);

    public async Task<ApiResult<ThemeExtractResultDto>> ExtractThemeColorsAsync(string url)
        => await PostAsync<ThemeExtractResultDto>($"api/beheer/theme/extract?url={Uri.EscapeDataString(url)}", new { });

    // ── Feedback widget ──

    public async Task<ApiResult<FeedbackValidateResponse>> ValidateFeedbackAsync(FeedbackValidateRequest dto)
        => await PostAsync<FeedbackValidateResponse>("api/feedback/validate", dto);

    public async Task<ApiResult<FeedbackSubmitResponse>> SubmitFeedbackAsync(FeedbackValidateRequest dto)
        => await PostAsync<FeedbackSubmitResponse>("api/feedback/submit", dto);

    // ── HTTP-helpers ──

    private async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await HandleAsync<T>(await send());
        }
        catch (Exception ex) { return ApiResult<T>.Fail(ex.Message); }
    }

    private Task<ApiResult<T>> GetAsync<T>(string path) => SendAsync<T>(() => _http.GetAsync(path));
    private Task<ApiResult<T>> PostAsync<T>(string path, object body) => SendAsync<T>(() => _http.PostAsJsonAsync(path, body));
    private Task<ApiResult<T>> PutAsync<T>(string path, object body) => SendAsync<T>(() => _http.PutAsJsonAsync(path, body));
    private Task<ApiResult<T>> DeleteAsync<T>(string path) => SendAsync<T>(() => _http.DeleteAsync(path));

    // ── Velden (#679) ──
    public async Task<ApiResult<List<VeldDto>>> GetVeldenAsync()
        => await GetAsync<List<VeldDto>>("api/beheer/velden");

    public async Task<ApiResult<object>> CreateVeldAsync(VeldDto dto)
        => await PostAsync<object>("api/beheer/velden", dto);

    public async Task<ApiResult<object>> UpdateVeldAsync(int veldNummer, VeldDto dto)
        => await PutAsync<object>($"api/beheer/velden/{veldNummer}", dto);

    // ── VeldBeschikbaarheid (#679: eerste GUI voor deze al bestaande API) ──
    public async Task<ApiResult<List<VeldBeschikbaarheidDto>>> GetVeldBeschikbaarheidAsync()
        => await GetAsync<List<VeldBeschikbaarheidDto>>("api/beheer/veldbeschikbaarheid");

    public async Task<ApiResult<object>> CreateVeldBeschikbaarheidAsync(VeldBeschikbaarheidDto dto)
        => await PostAsync<object>("api/beheer/veldbeschikbaarheid", dto);

    public async Task<ApiResult<object>> UpdateVeldBeschikbaarheidAsync(int id, VeldBeschikbaarheidDto dto)
        => await PutAsync<object>($"api/beheer/veldbeschikbaarheid/{id}", dto);

    public async Task<ApiResult<object>> DeleteVeldBeschikbaarheidAsync(int id)
        => await DeleteAsync<object>($"api/beheer/veldbeschikbaarheid/{id}");

    // ── VeldPeriode (#581: herbruikbare regimes zoals "Zomerstop" en "Competitie") ──
    public async Task<ApiResult<List<VeldPeriodeDto>>> GetVeldPeriodesAsync()
        => await GetAsync<List<VeldPeriodeDto>>("api/beheer/veldperiodes");

    public async Task<ApiResult<object>> CreateVeldPeriodeAsync(VeldPeriodeDto dto)
        => await PostAsync<object>("api/beheer/veldperiodes", dto);

    public async Task<ApiResult<object>> UpdateVeldPeriodeAsync(int id, VeldPeriodeDto dto)
        => await PutAsync<object>($"api/beheer/veldperiodes/{id}", dto);

    public async Task<ApiResult<object>> DeleteVeldPeriodeAsync(int id)
        => await DeleteAsync<object>($"api/beheer/veldperiodes/{id}");

    // ── VeldTraining (#679: trainingsschema per veld per weekdag) ──
    public async Task<ApiResult<List<VeldTrainingDto>>> GetVeldTrainingAsync()
        => await GetAsync<List<VeldTrainingDto>>("api/beheer/veldtraining");

    public async Task<ApiResult<object>> CreateVeldTrainingAsync(VeldTrainingDto dto)
        => await PostAsync<object>("api/beheer/veldtraining", dto);

    public async Task<ApiResult<object>> UpdateVeldTrainingAsync(int id, VeldTrainingDto dto)
        => await PutAsync<object>($"api/beheer/veldtraining/{id}", dto);

    public async Task<ApiResult<object>> DeleteVeldTrainingAsync(int id)
        => await DeleteAsync<object>($"api/beheer/veldtraining/{id}");

    // ── Test data / ALLSTARS (#365) ──
    public async Task<ApiResult<List<AllstarsWedstrijdDto>>> GetAllstarsWedstrijdenAsync()
        => await GetAsync<List<AllstarsWedstrijdDto>>("api/beheer/testdata/wedstrijden");
    public async Task<ApiResult<List<string>>> GetAllstarsTeamsAsync()
        => await GetAsync<List<string>>("api/beheer/testdata/teams");
    public async Task<ApiResult<object>> UpsertAllstarsWedstrijdAsync(AllstarsWedstrijdDto dto)
        => await PostAsync<object>("api/beheer/testdata/wedstrijden", dto);
    public async Task<ApiResult<object>> DeleteAllstarsWedstrijdAsync(string bk)
        => await DeleteAsync<object>($"api/beheer/testdata/wedstrijden/{Uri.EscapeDataString(bk)}");
    public async Task<ApiResult<object>> DeleteAlleAllstarsWedstrijdenAsync(string? van, string? tot)
    {
        var q = new List<string>();
        if (!string.IsNullOrEmpty(van)) q.Add($"van={Uri.EscapeDataString(van)}");
        if (!string.IsNullOrEmpty(tot)) q.Add($"tot={Uri.EscapeDataString(tot)}");
        var qs = q.Count > 0 ? "?" + string.Join("&", q) : "";
        return await DeleteAsync<object>($"api/beheer/testdata/wedstrijden{qs}");
    }
    public async Task<ApiResult<AllstarsVerplaatsDatumResultaat>> VerplaatsAllstarsDatumAsync(string oudeDatum, string nieuweDatum)
        => await PostAsync<AllstarsVerplaatsDatumResultaat>("api/beheer/testdata/wedstrijden/verplaats-datum",
               new { oudeDatum, nieuweDatum });

    private static async Task<ApiResult<T>> HandleAsync<T>(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return ApiResult<T>.Fail($"HTTP {(int)resp.StatusCode}: {text}", (int)resp.StatusCode);

        if (string.IsNullOrWhiteSpace(text))
            return ApiResult<T>.Ok(default!, (int)resp.StatusCode);

        try
        {
            var data = JsonSerializer.Deserialize<T>(text, _jsonOpts);
            return ApiResult<T>.Ok(data!, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            return ApiResult<T>.Fail($"Deserialisatie mislukt: {ex.Message}");
        }
    }
}
