namespace BlazorAdmin.Services;

/// <summary>
/// Keuzelijsten en vertaling van de filters van de pagina E-maillog (#1583): periode naar een begindatum en een
/// statuskeuze naar de querywaarde. Puur, zodat het los te testen is (geen logica in de Razor-pagina).
/// </summary>
public static class EmailLogFilter
{
    /// <summary>Maximaal aantal regels dat de server per aanvraag teruggeeft.</summary>
    public const int MaxRegels = 200;

    /// <summary>Periodekeuzes (sleutel, label). De sleutel is wat in de keuzelijst staat.</summary>
    public static readonly IReadOnlyList<(string Sleutel, string Label)> Perioden = new[]
    {
        ("24u", "Laatste 24 uur"),
        ("7d", "Laatste 7 dagen"),
        ("30d", "Laatste 30 dagen"),
        ("alles", "Alles (maximaal 200 regels)")
    };

    /// <summary>Statuskeuzes (waarde zoals de server hem kent, label). Lege waarde = alle statussen.</summary>
    public static readonly IReadOnlyList<(string Waarde, string Label)> Statussen = new[]
    {
        ("", "Alle statussen"),
        ("Review", "Review (wacht op beoordeling)"),
        ("AntwoordVerstuurd", "Antwoord verstuurd"),
        ("GeenAntwoordNodig", "Handmatige planning (geen antwoord)"),
        ("Fout", "Fout"),
        ("BuitenScope", "Buiten scope")
    };

    /// <summary>
    /// Begindatum (lokale dag) voor een periodesleutel; <c>null</c> = geen ondergrens. De server rekent per dag, dus
    /// "laatste 24 uur" is gisteren t/m vandaag — dezelfde keuze als de teller op Instellingen.
    /// </summary>
    public static DateTime? Vanaf(string? periode, DateTime vandaag) => periode switch
    {
        "24u" => vandaag.Date.AddDays(-1),
        "7d" => vandaag.Date.AddDays(-7),
        "30d" => vandaag.Date.AddDays(-30),
        _ => null
    };

    /// <summary>Lege of onbekende status telt als "alle"; een bekende status gaat ongewijzigd door.</summary>
    public static string? StatusParameter(string? status)
        => Statussen.Any(s => s.Waarde.Length > 0 && s.Waarde == status) ? status : null;
}
