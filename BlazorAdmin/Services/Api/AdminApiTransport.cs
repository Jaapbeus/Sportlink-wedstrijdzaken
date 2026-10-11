using System.Net.Http.Json;
using System.Text.Json;
using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Gedeelde transportkern onder de domeinclients (<c>InstellingenApiClient</c>, <c>PlanningApiClient</c>, ...; #1630):
/// één plek voor HTTP-aanroepen, statusafhandeling, foutteksten, JSON-opties en telemetrie van mislukte aanroepen.
/// De <see cref="HttpClient"/> wordt in <c>Program.cs</c> samengesteld (BaseUrl uit appsettings, X-Club-Code-handler en,
/// bij Entra, de Bearer-token-handler); de SWA proxyt niet: de browser roept de Function App rechtstreeks aan (CORS).
/// </summary>
public sealed class AdminApiTransport
{
    private readonly HttpClient _http;
    private readonly ClientTelemetryService? _telemetrie;
    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);

    public AdminApiTransport(HttpClient http, ClientTelemetryService? telemetrie = null)
    {
        _http = http;
        _telemetrie = telemetrie;
    }

    public Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken = default)
        => SendAsync<T>("GET", path, () => _http.GetAsync(path, cancellationToken));

    public Task<ApiResult<T>> PostAsync<T>(string path, object body)
        => SendAsync<T>("POST", path, () => _http.PostAsJsonAsync(path, body));

    public Task<ApiResult<T>> PutAsync<T>(string path, object body)
        => SendAsync<T>("PUT", path, () => _http.PutAsJsonAsync(path, body));

    public Task<ApiResult<T>> DeleteAsync<T>(string path)
        => SendAsync<T>("DELETE", path, () => _http.DeleteAsync(path));

    // Deel-endpoints (HTML/PDF): geen ApiResult maar een worp bij een mislukte aanroep; het DeelPaneel vangt die op.
    public Task<string> GetTekstAsync(string path) => LeesTekstAsync(_http.GetAsync(path));

    public Task<byte[]> GetBytesAsync(string path) => LeesBytesAsync(_http.GetAsync(path));

    public Task<string> PostTekstAsync(string path, object body) => LeesTekstAsync(_http.PostAsJsonAsync(path, body));

    public Task<byte[]> PostBytesAsync(string path, object body) => LeesBytesAsync(_http.PostAsJsonAsync(path, body));

    private async Task<ApiResult<T>> SendAsync<T>(string methode, string pad, Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            var resp = await send();
            var result = await HandleAsync<T>(resp);
            if (!result.Success)
                _telemetrie?.MeldMislukteAanroep(methode, pad, (int)resp.StatusCode, CorrelatieId(resp));
            return result;
        }
        // #1136: een bewust geannuleerde aanroep (aanroeper heeft een nieuwere lookup gestart)
        // hoort geen foutmelding op te leveren — de aanroeper gooit dit resultaat toch weg.
        catch (OperationCanceledException) { return ApiResult<T>.Fail("Geannuleerd"); }
        catch (Exception ex)
        {
            // Netwerkfout zonder antwoord: status 0 (#764 — technische context bij feedback).
            _telemetrie?.MeldMislukteAanroep(methode, pad, 0, null);
            return ApiResult<T>.Fail(ex.Message);
        }
    }

    private static string? CorrelatieId(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("x-correlation-id", out var waarden) ? waarden.FirstOrDefault() : null;

    private static async Task<ApiResult<T>> HandleAsync<T>(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return ApiResult<T>.Fail(FoutTekst((int)resp.StatusCode, text), (int)resp.StatusCode, FoutCode(text));

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

    /// <summary>
    /// Foutmelding voor de gebruiker uit een foutrespons: bij een JSON-body met een
    /// <c>error</c>-tekst alleen die tekst, anders een neutrale melding met de statuscode — nooit ruwe JSON.
    /// </summary>
    public static string FoutTekst(int statusCode, string? body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var fout) &&
                    fout.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(fout.GetString()))
                    return fout.GetString()!;
            }
            catch (JsonException)
            {
                // Geen JSON: terugval hieronder.
            }
        }
        return $"Het verzoek is mislukt (HTTP {statusCode}).";
    }

    /// <summary>Leest het optionele <c>code</c>-veld uit een JSON-foutrespons; <c>null</c> als het ontbreekt of de body geen JSON is.</summary>
    public static string? FoutCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
