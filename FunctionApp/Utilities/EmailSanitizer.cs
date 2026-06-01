namespace SportlinkFunction;

/// <summary>
/// Utility voor het anonimiseren van foutmeldingen vóór DB-opslag of logging.
/// Internal zodat unit tests via InternalsVisibleTo toegang hebben. (#476)
/// </summary>
internal static class EmailSanitizer
{
    // Verwijdert e-mailadressen en knipt af op 200 tekens. (#420)
    internal static string SanitizeFoutMelding(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Onbekende fout";
        var gesaneerd = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
            "[e-mail]");
        return gesaneerd.Length > 200 ? gesaneerd[..200] + "…" : gesaneerd;
    }
}
