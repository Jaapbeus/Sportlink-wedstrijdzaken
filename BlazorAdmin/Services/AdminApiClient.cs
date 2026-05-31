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
    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);

    public AdminApiClient(HttpClient http)
    {
        _http = http;
    }

    // ── Settings ──

    public async Task<ApiResult<AppSettingsDto>> GetSettingsAsync()
        => await GetAsync<AppSettingsDto>("api/beheer/settings");

    public async Task<ApiResult<SettingsUpdateResultDto>> UpdateSettingsAsync(SettingsUpdateDto dto)
        => await PutAsync<SettingsUpdateResultDto>("api/beheer/settings", dto);

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

    // ── Dagplanning / Optimaliseer ──

    public async Task<ApiResult<OptimaliseerResponseDto>> OptimaliseerAsync(OptimaliseerRequestDto req)
        => await PostAsync<OptimaliseerResponseDto>("api/planner/optimaliseer", req);

    public async Task<ApiResult<string>> OptimaliseerHtmlAsync(OptimaliseerRequestDto req, string format)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"api/planner/optimaliseer?format={format}", req);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return ApiResult<string>.Fail($"HTTP {(int)resp.StatusCode}: {text}", (int)resp.StatusCode);
            return ApiResult<string>.Ok(text, (int)resp.StatusCode);
        }
        catch (Exception ex) { return ApiResult<string>.Fail(ex.Message); }
    }

    // ── Auto-plan (#380) ──

    public async Task<ApiResult<AutoPlanResponseDto>> AutoPlanAsync(AutoPlanRequestDto req)
        => await PostAsync<AutoPlanResponseDto>("api/planner/auto-plan", req);

    public async Task<ApiResult<AutoPlanToepassenResponseDto>> AutoPlanToepassenAsync(AutoPlanToepassenRequestDto req)
        => await PostAsync<AutoPlanToepassenResponseDto>("api/planner/auto-plan/toepassen", req);

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

    private async Task<ApiResult<T>> GetAsync<T>(string path)
    {
        try
        {
            var resp = await _http.GetAsync(path);
            return await HandleAsync<T>(resp);
        }
        catch (Exception ex) { return ApiResult<T>.Fail(ex.Message); }
    }

    private async Task<ApiResult<T>> PostAsync<T>(string path, object body)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(path, body);
            return await HandleAsync<T>(resp);
        }
        catch (Exception ex) { return ApiResult<T>.Fail(ex.Message); }
    }

    private async Task<ApiResult<T>> PutAsync<T>(string path, object body)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync(path, body);
            return await HandleAsync<T>(resp);
        }
        catch (Exception ex) { return ApiResult<T>.Fail(ex.Message); }
    }

    private async Task<ApiResult<T>> DeleteAsync<T>(string path)
    {
        try
        {
            var resp = await _http.DeleteAsync(path);
            return await HandleAsync<T>(resp);
        }
        catch (Exception ex) { return ApiResult<T>.Fail(ex.Message); }
    }

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
