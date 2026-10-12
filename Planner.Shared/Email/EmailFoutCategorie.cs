namespace Planner.Shared.Email;

/// <summary>
/// Privacy-veilige foutherkenning voor de e-mailverwerker (#425, gedeeld sinds #1650). Werkt
/// uitsluitend op de tekst van de exception, dus zonder databasetype.
/// </summary>
public static class EmailFoutCategorie
{
    /// <summary>Is dit een OpenAI-quotafout (quota op of HTTP 429)? Stuurt het afbreken van de AI-batch.</summary>
    public static bool IsOpenAiQuotaFout(Exception ex)
    {
        var msg = ex.Message + (ex.InnerException?.Message ?? "");
        return msg.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("429", StringComparison.Ordinal);
    }

    /// <summary>
    /// Categoriseert een exception naar een privacy-safe foutomschrijving. Nooit ruwe
    /// <c>ex.Message</c> in noodmails of externe output — kan PII bevatten.
    /// </summary>
    public static string Categoriseer(Exception ex)
    {
        var msg = (ex.Message + (ex.InnerException?.Message ?? "")).ToLowerInvariant();
        if (msg.Contains("insufficient_quota") || msg.Contains("429"))
            return "OpenAI quota overschreden";
        if (msg.Contains("login failed") || msg.Contains("cannot open database") || msg.Contains("connection"))
            return "Database niet beschikbaar";
        if (msg.Contains("404") || msg.Contains("resourcenotfound") || msg.Contains("not found"))
            return "Graph API: bericht niet gevonden";
        if (msg.Contains("401") || msg.Contains("unauthorized") || msg.Contains("403") || msg.Contains("forbidden"))
            return "Graph API: autorisatiefout";
        if (msg.Contains("timeout") || msg.Contains("timed out"))
            return "Time-out bij externe service";
        return "Onverwachte verwerkingsfout";
    }
}
