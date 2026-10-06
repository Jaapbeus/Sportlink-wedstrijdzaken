using System.Text.Json;

namespace BlazorAdmin.Services;

public partial class AdminApiClient
{
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
