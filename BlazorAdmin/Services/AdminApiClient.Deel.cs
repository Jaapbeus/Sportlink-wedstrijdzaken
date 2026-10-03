using System.Net.Http.Json;
using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Deel-endpoints (#1364, epic #1365): dezelfde routes als de JSON-aanroepen, met
/// <c>?format=html|pdf</c>. Eigen bestand omdat <c>AdminApiClient.cs</c> tegen de 500-regelsgrens zit.
/// <para>
/// Deze methodes gooien bij een mislukte aanroep (het <c>DeelPaneel</c> vangt dat op en toont de
/// melding) in plaats van een <see cref="ApiResult{T}"/> terug te geven: het paneel verwacht
/// <c>Task&lt;string&gt;</c> / <c>Task&lt;byte[]&gt;</c>.
/// </para>
/// </summary>
public partial class AdminApiClient
{
    public Task<string> GetVeldbezettingHtmlAsync(string datum)
        => LeesTekstAsync(_http.GetAsync($"api/planner/veldbezetting?datum={Uri.EscapeDataString(datum)}&format=html"));

    public Task<byte[]> GetVeldbezettingPdfAsync(string datum)
        => LeesBytesAsync(_http.GetAsync($"api/planner/veldbezetting?datum={Uri.EscapeDataString(datum)}&format=pdf"));

    /// <param name="tab"><c>huidig</c> of <c>optimaal</c> — de tab die de gebruiker op Veld optimalisatie ziet.</param>
    public Task<string> GetAutoPlanHtmlAsync(string datum, int? bufferMinuten, string tab)
        => LeesTekstAsync(_http.PostAsJsonAsync(AutoPlanDeelPad("html", tab), new AutoPlanRequestDto { Datum = datum, BufferMinuten = bufferMinuten }));

    public Task<byte[]> GetAutoPlanPdfAsync(string datum, int? bufferMinuten, string tab)
        => LeesBytesAsync(_http.PostAsJsonAsync(AutoPlanDeelPad("pdf", tab), new AutoPlanRequestDto { Datum = datum, BufferMinuten = bufferMinuten }));

    /// <summary>
    /// #1460: deelt de planning zoals die op het scherm staat, inclusief handmatig versleepte
    /// blokken. Stateless: de server valideert en rendert alleen wat hier meegestuurd wordt.
    /// </summary>
    public Task<string> GetAutoPlanDeelHtmlAsync(string datum, string tab, IEnumerable<AutoPlanDeelRegelDto> regels)
        => LeesTekstAsync(_http.PostAsJsonAsync(AutoPlanGetoondPad("html"), new AutoPlanDeelRequestDto { Datum = datum, Tab = tab, Wedstrijden = regels.ToList() }));

    public Task<byte[]> GetAutoPlanDeelPdfAsync(string datum, string tab, IEnumerable<AutoPlanDeelRegelDto> regels)
        => LeesBytesAsync(_http.PostAsJsonAsync(AutoPlanGetoondPad("pdf"), new AutoPlanDeelRequestDto { Datum = datum, Tab = tab, Wedstrijden = regels.ToList() }));

    private static string AutoPlanGetoondPad(string format) => $"api/planner/auto-plan/deel?format={format}";

    private static string AutoPlanDeelPad(string format, string tab)
        => $"api/planner/auto-plan?format={format}&tab={Uri.EscapeDataString(tab)}";

    private static async Task<string> LeesTekstAsync(Task<HttpResponseMessage> aanroep)
    {
        using var resp = await aanroep;
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        return await resp.Content.ReadAsStringAsync();
    }

    private static async Task<byte[]> LeesBytesAsync(Task<HttpResponseMessage> aanroep)
    {
        using var resp = await aanroep;
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        return await resp.Content.ReadAsByteArrayAsync();
    }
}
