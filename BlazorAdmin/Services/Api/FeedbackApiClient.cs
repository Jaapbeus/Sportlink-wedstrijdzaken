using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Feedbackwidget en feedbackoverzicht voor beheerders (#764, #1205, #1478). HTTP, status- en foutafhandeling zit in <see cref="AdminApiTransport"/> (#1630).</summary>
public sealed class FeedbackApiClient
{
    private readonly AdminApiTransport _t;

    public FeedbackApiClient(AdminApiTransport transport) => _t = transport;

    // ── Feedback widget ──

    public async Task<ApiResult<FeedbackValidateResponse>> ValidateFeedbackAsync(FeedbackValidateRequest dto)
        => await _t.PostAsync<FeedbackValidateResponse>("api/feedback/validate", dto);

    /// <summary>
    /// Haalt de exacte titel + body op die gepubliceerd zou worden, zonder iets aan te maken (#1205).
    /// </summary>
    public async Task<ApiResult<FeedbackPreviewResponse>> PreviewFeedbackAsync(FeedbackValidateRequest dto)
        => await _t.PostAsync<FeedbackPreviewResponse>("api/feedback/preview", dto);

    public async Task<ApiResult<FeedbackSubmitResponse>> SubmitFeedbackAsync(FeedbackValidateRequest dto)
        => await _t.PostAsync<FeedbackSubmitResponse>("api/feedback/submit", dto);

    // ── Feedbackoverzicht voor beheerders (#764, #1478) ──

    public async Task<ApiResult<FeedbackLijstDto>> GetFeedbackLijstAsync(FeedbackFilterDto filter)
        => await _t.GetAsync<FeedbackLijstDto>("api/beheer/feedback" + filter.NaarQuerystring());

    public async Task<ApiResult<FeedbackDetailDto>> GetFeedbackDetailAsync(Guid id)
        => await _t.GetAsync<FeedbackDetailDto>($"api/beheer/feedback/{id}");

    public async Task<ApiResult<FeedbackPubliceerResponse>> PubliceerFeedbackAsync(Guid id)
        => await _t.PostAsync<FeedbackPubliceerResponse>($"api/beheer/feedback/{id}/publiceer", new { });

    public async Task<ApiResult<FeedbackInzagelogDto>> GetFeedbackInzagelogAsync(int limit = 50, int offset = 0)
        => await _t.GetAsync<FeedbackInzagelogDto>($"api/beheer/feedback/inzagelog?limit={limit}&offset={offset}");
}
