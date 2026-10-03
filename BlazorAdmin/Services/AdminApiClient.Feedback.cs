using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Feedbackwidget en feedbackoverzicht (#764) — eigen bestand om <c>AdminApiClient.cs</c> onder de 500 regels te houden.</summary>
public partial class AdminApiClient
{
    // ── Feedback widget ──

    public async Task<ApiResult<FeedbackValidateResponse>> ValidateFeedbackAsync(FeedbackValidateRequest dto)
        => await PostAsync<FeedbackValidateResponse>("api/feedback/validate", dto);

    /// <summary>
    /// Haalt de exacte titel + body op die gepubliceerd zou worden, zonder iets aan te maken (#1205).
    /// </summary>
    public async Task<ApiResult<FeedbackPreviewResponse>> PreviewFeedbackAsync(FeedbackValidateRequest dto)
        => await PostAsync<FeedbackPreviewResponse>("api/feedback/preview", dto);

    public async Task<ApiResult<FeedbackSubmitResponse>> SubmitFeedbackAsync(FeedbackValidateRequest dto)
        => await PostAsync<FeedbackSubmitResponse>("api/feedback/submit", dto);

    // ── Feedbackoverzicht voor beheerders (#764, #1478) ──

    public async Task<ApiResult<FeedbackLijstDto>> GetFeedbackLijstAsync(FeedbackFilterDto filter)
        => await GetAsync<FeedbackLijstDto>("api/beheer/feedback" + filter.NaarQuerystring());

    public async Task<ApiResult<FeedbackDetailDto>> GetFeedbackDetailAsync(Guid id)
        => await GetAsync<FeedbackDetailDto>($"api/beheer/feedback/{id}");

    public async Task<ApiResult<FeedbackPubliceerResponse>> PubliceerFeedbackAsync(Guid id)
        => await PostAsync<FeedbackPubliceerResponse>($"api/beheer/feedback/{id}/publiceer", new { });

    public async Task<ApiResult<FeedbackInzagelogDto>> GetFeedbackInzagelogAsync(int limit = 50, int offset = 0)
        => await GetAsync<FeedbackInzagelogDto>($"api/beheer/feedback/inzagelog?limit={limit}&offset={offset}");
}
