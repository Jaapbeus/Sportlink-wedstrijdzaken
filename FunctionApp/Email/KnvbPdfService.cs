using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

/// <summary>
/// Leest de KNVB-speeldagenkalender-PDF's die als Content-bestanden meereizen in het Function
/// App-deploymentpakket (#561) — geen nieuwe Azure-resource, dus kosten blijven €0.
///
/// <para>
/// Fail-safe: een ontbrekend bestand, een onbekende regio, of een leesfout levert altijd
/// <c>null</c> op (nooit een exception) — een klein bijlage-probleem mag de hoofdverwerking van een
/// e-mail nooit laten crashen.
/// </para>
/// </summary>
public static class KnvbPdfService
{
    private const string ContentType = "application/pdf";

    // Bytes-cache per (regio, seizoen): PDF's zijn seizoensgebonden en veranderen tijdens het leven
    // van het proces niet, dus hoeven maar één keer van disk gelezen te worden.
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new();

    /// <summary>
    /// Regio → bestandsnaam-slug. Uitputtende lijst — een regio die hier niet in staat, staat ook
    /// niet in <c>dbo.KnvbKalenderDag</c>/AppSettings-check-constraint en levert bewust <c>null</c> op.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RegioSlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["West"] = "west",
        ["Noord"] = "noord",
        ["Oost"] = "oost",
        ["Zuid"] = "zuid",
        ["Landelijk"] = "landelijk",
        ["LandelijkJeugd"] = "landelijk-jeugd"
    };

    /// <summary>
    /// Leest de speeldagenkalender-PDF voor de opgegeven regio en het opgegeven seizoen
    /// (bijv. seizoen "2026/2027"). Retourneert <c>null</c> als het bestand niet gevonden of niet
    /// leesbaar is, of als de regio onbekend is — nooit een exception.
    /// </summary>
    public static async Task<EmailBijlage?> GetKalenderPdfAsync(string regio, string seizoen, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(regio) || string.IsNullOrWhiteSpace(seizoen))
        {
            log.LogWarning("KNVB-PDF - regio of seizoen ontbreekt, geen bijlage toegevoegd");
            return null;
        }

        if (!RegioSlug.TryGetValue(regio, out var slug))
        {
            log.LogWarning("KNVB-PDF - onbekende regio, geen bijlage toegevoegd");
            return null;
        }

        // "2026/2027" -> "2026-2027"
        var seizoenSlug = seizoen.Replace('/', '-');
        var bestandsnaam = $"speeldagenkalender-veld-{slug}-{seizoenSlug}.pdf";
        var cacheKey = $"{seizoenSlug}/{bestandsnaam}";

        if (_cache.TryGetValue(cacheKey, out var cachedBytes))
            return new EmailBijlage(bestandsnaam, cachedBytes, ContentType);

        try
        {
            var pad = Path.Combine(AppContext.BaseDirectory, "Content", "KnvbKalenders", seizoenSlug, bestandsnaam);
            if (!File.Exists(pad))
            {
                log.LogWarning("KNVB-PDF - bestand niet gevonden voor het opgegeven seizoen/regio, geen bijlage toegevoegd");
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(pad);
            _cache[cacheKey] = bytes;
            return new EmailBijlage(bestandsnaam, bytes, ContentType);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "KNVB-PDF - leesfout, geen bijlage toegevoegd");
            return null;
        }
    }
}
