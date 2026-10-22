using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Auto-plan, veldbezetting en de deel-endpoints (HTML/PDF, #1364, #1459, #1460). De deelmethodes gooien bij een mislukte aanroep (het <c>DeelPaneel</c> vangt dat op). HTTP, status- en foutafhandeling zit in <see cref="AdminApiTransport"/> (#1630).</summary>
public sealed class PlanningApiClient
{
    private readonly AdminApiTransport _t;

    public PlanningApiClient(AdminApiTransport transport) => _t = transport;

    // ── Auto-plan (#380) — de enige dagplanning-optimalisatie sinds #666 ──

    public async Task<ApiResult<AutoPlanResponseDto>> AutoPlanAsync(AutoPlanRequestDto req)
        => await _t.PostAsync<AutoPlanResponseDto>("api/planner/auto-plan", req);

    public async Task<ApiResult<AutoPlanToepassenResponseDto>> AutoPlanToepassenAsync(AutoPlanToepassenRequestDto req)
        => await _t.PostAsync<AutoPlanToepassenResponseDto>("api/planner/auto-plan/toepassen", req);

    /// <summary>Vraagt met <c>status=true</c> expliciet de statusenvelope aan (#1774); zonder die parameter blijft de route de kale array leveren voor oudere clients.</summary>
    public async Task<ApiResult<VeldbezettingAntwoordDto>> GetVeldbezettingAsync(string datum)
        => await _t.GetAsync<VeldbezettingAntwoordDto>($"api/planner/veldbezetting?datum={Uri.EscapeDataString(datum)}&status=true");

    public Task<string> GetVeldbezettingHtmlAsync(string datum)
        => _t.GetTekstAsync($"api/planner/veldbezetting?datum={Uri.EscapeDataString(datum)}&format=html");

    public Task<byte[]> GetVeldbezettingPdfAsync(string datum)
        => _t.GetBytesAsync($"api/planner/veldbezetting?datum={Uri.EscapeDataString(datum)}&format=pdf");

    /// <param name="tab"><c>huidig</c> of <c>optimaal</c> — de tab die de gebruiker op Veld optimalisatie ziet.</param>
    public Task<string> GetAutoPlanHtmlAsync(string datum, int? bufferMinuten, string tab)
        => _t.PostTekstAsync(AutoPlanDeelPad("html", tab), new AutoPlanRequestDto { Datum = datum, BufferMinuten = bufferMinuten });

    public Task<byte[]> GetAutoPlanPdfAsync(string datum, int? bufferMinuten, string tab)
        => _t.PostBytesAsync(AutoPlanDeelPad("pdf", tab), new AutoPlanRequestDto { Datum = datum, BufferMinuten = bufferMinuten });

    /// <summary>
    /// #1460: deelt de planning zoals die op het scherm staat, inclusief handmatig versleepte
    /// blokken. Stateless: de server valideert en rendert alleen wat hier meegestuurd wordt.
    /// </summary>
    public Task<string> GetAutoPlanDeelHtmlAsync(string datum, string tab, IEnumerable<AutoPlanDeelRegelDto> regels)
        => _t.PostTekstAsync(AutoPlanGetoondPad("html"), new AutoPlanDeelRequestDto { Datum = datum, Tab = tab, Wedstrijden = regels.ToList() });

    public Task<byte[]> GetAutoPlanDeelPdfAsync(string datum, string tab, IEnumerable<AutoPlanDeelRegelDto> regels)
        => _t.PostBytesAsync(AutoPlanGetoondPad("pdf"), new AutoPlanDeelRequestDto { Datum = datum, Tab = tab, Wedstrijden = regels.ToList() });

    private static string AutoPlanGetoondPad(string format) => $"api/planner/auto-plan/deel?format={format}";

    private static string AutoPlanDeelPad(string format, string tab)
        => $"api/planner/auto-plan?format={format}&tab={Uri.EscapeDataString(tab)}";

    /// <summary>
    /// #1459: of PDF-export voor de gekozen club aan staat (<c>GET api/planner/pdf-export</c>, open voor
    /// elke ingelogde rol). Een mislukte aanroep telt als uit — fail-closed, net als de server.
    /// </summary>
    public async Task<bool> IsPdfExportIngeschakeldAsync()
    {
        var status = await _t.GetAsync<PdfExportStatusDto>("api/planner/pdf-export");
        return status.Success && status.Data?.PdfExportIngeschakeld == true;
    }
}
