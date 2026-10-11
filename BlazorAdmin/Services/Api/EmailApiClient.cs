using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>E-mailtemplates, uitgesloten adressen, e-maillog/trace en de e-mailtester. HTTP, status- en foutafhandeling zit in <see cref="AdminApiTransport"/> (#1630).</summary>
public sealed class EmailApiClient
{
    private readonly AdminApiTransport _t;

    public EmailApiClient(AdminApiTransport transport) => _t = transport;

    // ── Templates ──

    public async Task<ApiResult<List<TemplateDto>>> GetTemplatesAsync()
        => await _t.GetAsync<List<TemplateDto>>("api/beheer/templates");

    public async Task<ApiResult<object>> UpdateTemplateAsync(string key, TemplateDto dto)
        => await _t.PutAsync<object>($"api/beheer/templates/{Uri.EscapeDataString(key)}", dto);

    public async Task<ApiResult<object>> ResetTemplateAsync(string key)
        => await _t.PostAsync<object>($"api/beheer/templates/{Uri.EscapeDataString(key)}/reset", new { });

    // ── Uitgesloten e-mailadressen ──

    public async Task<ApiResult<List<UitgeslotenEmailAdresDto>>> GetUitgeslotenEmailsAsync()
        => await _t.GetAsync<List<UitgeslotenEmailAdresDto>>("api/beheer/uitgesloten-emails");

    public async Task<ApiResult<object>> CreateUitgeslotenEmailAsync(UitgeslotenEmailAdresDto dto)
        => await _t.PostAsync<object>("api/beheer/uitgesloten-emails", dto);

    public async Task<ApiResult<object>> DeleteUitgeslotenEmailAsync(int id)
        => await _t.DeleteAsync<object>($"api/beheer/uitgesloten-emails/{id}");

    // ── Email log ──

    public async Task<ApiResult<EmailLogResponse>> GetEmailLogAsync(
        DateTime? vanaf = null, DateTime? tot = null, string? status = null, int limit = 50)
    {
        var qp = new List<string> { $"limit={limit}" };
        if (vanaf.HasValue) qp.Add($"vanaf={vanaf:yyyy-MM-dd}");
        if (tot.HasValue) qp.Add($"tot={tot:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(status)) qp.Add($"status={Uri.EscapeDataString(status)}");
        return await _t.GetAsync<EmailLogResponse>("api/beheer/email-log?" + string.Join("&", qp));
    }

    public async Task<ApiResult<EmailTraceDto>> GetEmailTraceAsync(int verwerkingId)
        => await _t.GetAsync<EmailTraceDto>($"api/beheer/email-log/{verwerkingId}/trace");

    // ── Email tester ──

    public async Task<ApiResult<TestEmailResponse>> TestEmailAsync(TestEmailRequest dto)
        => await _t.PostAsync<TestEmailResponse>("api/test/email", dto);
}
