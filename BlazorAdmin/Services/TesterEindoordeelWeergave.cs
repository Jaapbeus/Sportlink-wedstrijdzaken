using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Opmaak van het eindoordeel van de e-mailtester (#1583). De inhoud komt van de server
/// (<c>Planner.Shared.Email.Trace.TesterEindoordeel</c>); hier staat alleen de vertaling naar kleur en kop.
/// </summary>
public static class TesterEindoordeelWeergave
{
    public const string Review = "Review";
    public const string AutomatischVerstuurd = "AutomatischVerstuurd";
    public const string GeenAntwoord = "GeenAntwoord";

    private const string OudeKop = "Voorbeeld-antwoord (zou-worden-verstuurd)";

    /// <summary>Review en een waarschuwing vallen op (geel), een gewone automatische verzending is groen, geen antwoord is neutraal.</summary>
    public static string AlertKlasse(TesterEindoordeelDto? eindoordeel) => eindoordeel switch
    {
        null => "alert alert-secondary",
        { Waarschuwing: true } => "alert alert-danger",
        { Uitkomst: Review } => "alert alert-warning",
        { Uitkomst: AutomatischVerstuurd } => "alert alert-success",
        _ => "alert alert-secondary"
    };

    /// <summary>Kop boven het voorbeeld-antwoord: duidelijk of het een concept voor review is of wat er verstuurd zou worden.</summary>
    public static string VoorbeeldKop(TesterEindoordeelDto? eindoordeel)
        => string.IsNullOrWhiteSpace(eindoordeel?.ConceptLabel) ? OudeKop : eindoordeel.ConceptLabel;
}
