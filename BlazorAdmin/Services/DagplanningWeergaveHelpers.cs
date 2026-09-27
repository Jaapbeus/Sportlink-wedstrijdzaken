using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>
/// Gedeelde Gantt-weergavelogica voor de Planning- en Veld-optimalisatie-pagina's (#1361), losgetrokken
/// uit wat vóór de paginasplitsing één keer in <c>Dagplanning.razor.cs</c> stond en door zowel de
/// veldbezettingstijdlijn als de optimaal/huidig-vergelijkingstijdlijn werd aangeroepen. Puur statisch,
/// geen DI-afhankelijkheden — zelfde opzet als <see cref="GanttLayout"/> (die blijft een aparte klasse:
/// dat is de top/hoogte-berekening van één balk, dit is de item-constructie/labels/tooltip).
/// </summary>
public static class DagplanningWeergaveHelpers
{
    /// <summary>
    /// Eén Gantt-balk. <paramref name="Bron"/> is null op de directe veldbezettingsweergave (Planning) en
    /// op de "Huidig"-tab van Veld optimalisatie — die standen zijn niet sleepbaar. Alleen de "Optimaal"-
    /// tab vult <c>Bron</c>, zodat een sleepactie de onderliggende <see cref="AutoPlanWedstrijdItemDto"/>
    /// kan bijwerken.
    /// </summary>
    public record GanttItem(string VeldNaam, string? SubPos, TimeOnly Aanvang, TimeOnly Einde,
        decimal Fractie, string Label, string Status, int DuurMinuten,
        string? VoorkeurTijd, int? VoorkeurAfwijking, AutoPlanWedstrijdItemDto? Bron = null,
        long? WedstrijdCode = null);

    // Alleen échte subposities van een gedeeld veld ("Kunstgras 1 A2") worden afgesplitst — precies de
    // waarden die de planner uitdeelt en die GanttLayout kan positioneren.
    //
    // De oude check ("laatste deel is maximaal 2 tekens en alfanumeriek") herkende óók een gewoon
    // veldnummer als subpositie: "Kunstgras 1" werd ("Kunstgras", "1"). Gevolg was tweeledig — alle
    // velden kregen dezelfde basisnaam en vielen samen op één tijdlijnrij (#665), en bij het verslepen
    // naar een ander veld werd dat nummer als subpositie aan de nieuwe veldnaam geplakt ("Gras 1").
    private static readonly string[] SubPosLabels = ["A", "A1", "A2", "B", "B1", "B2"];

    public static string GanttMatchLabel(string? wedstrijd, string teamNaam)
        => !string.IsNullOrWhiteSpace(wedstrijd) ? wedstrijd : teamNaam;

    public static string? GanttExtractSubPos(string? veld)
    {
        if (string.IsNullOrWhiteSpace(veld)) return null;
        var parts = veld.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var last = parts[^1];
        return SubPosLabels.Contains(last, StringComparer.OrdinalIgnoreCase) ? last.ToUpperInvariant() : null;
    }

    public static (string Base, string? SubPos) GanttSplitVeld(string veld)
    {
        var sub = GanttExtractSubPos(veld);
        if (sub == null) return (veld.Trim(), null);
        var idx = veld.LastIndexOf(' ');
        return idx > 0 ? (veld[..idx].Trim(), sub) : (veld.Trim(), null);
    }

    // Converteert een getal naar CSS-percentagestring (bijv. 42.5% → "42.50")
    public static string GanttPct(int deel, int totaal)
        => totaal > 0
            ? ((double)deel / totaal * 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            : "0.00";

    /// <summary>Uur-label op de Gantt-as. Bewust geen <c>TimeOnly.FromTimeSpan</c>: bij een wedstrijd die
    /// ná 23:00 eindigt loopt de as tot 1440 minuten, en dat is voor <c>TimeOnly</c> buiten bereik
    /// (ArgumentOutOfRangeException, #1107 bevinding 3).</summary>
    public static string GanttUurLabel(int minuut) => $"{minuut / 60:00}:{minuut % 60:00}";

    /// <summary>
    /// Horizontale uitlijning van een tijdlabel op de tijdas (#689): het eerste label lijnt links uit,
    /// het laatste rechts, alles ertussen blijft gecentreerd — zodat geen enkel label buiten de
    /// container valt en er geen onnodige horizontale scrollbalk ontstaat.
    /// </summary>
    public static string GanttLabelTransform(int uur, int startMinuut, int eindMinuut)
        => uur <= startMinuut ? "translateX(0)"
         : uur >= eindMinuut  ? "translateX(-100%)"
         : "translateX(-50%)";

    public static string GanttTooltip(GanttItem gi)
    {
        var sb = gi.Label
            + "\n" + gi.Aanvang.ToString("HH:mm") + " – " + gi.Einde.ToString("HH:mm")
            + " (" + gi.DuurMinuten + " min)";
        if (gi.VoorkeurTijd != null)
        {
            sb += "\nVoorkeur: " + gi.VoorkeurTijd;
            if (gi.VoorkeurAfwijking == 0)
                sb += " ✓ op voorkeurstijd";
            else if (gi.VoorkeurAfwijking.HasValue)
                sb += " (" + (gi.VoorkeurAfwijking > 0 ? "+" : "") + gi.VoorkeurAfwijking + " min)";
        }
        return sb;
    }

    // Verwijst sinds #1388 naar de instelbare paletsleutels (ThemePresets.Kleuren) in plaats van
    // vaste hex — de waarde gaat via een inline "--gantt-bg:...;"-style-attribuut naar de DOM
    // (zie VeldOptimalisatie.razor/Planning.razor), en een CSS var()-verwijzing is daar een geldige
    // waarde. Standaardwaarden van de paletsleutels zijn gelijk aan de oude hex-literals.
    public static string GanttKleur(string status) => status switch
    {
        "wijziging"   => "var(--theme-status-wijziging)",
        "nieuw-slot"  => "var(--theme-status-nieuw-slot)",
        "ongewijzigd" => "var(--theme-status-ongewijzigd)",
        _ => "var(--theme-secondary)"
    };

    /// <summary>
    /// Kleur van de voorkeurstijd-indicatorbalk bovenaan een Gantt-blok — alleen relevant in de
    /// optimaal/huidig-vergelijking van Veld optimalisatie, niet op de directe veldbezettingsweergave
    /// van Planning. Leeg = geen voorkeur geconfigureerd → geen balk.
    /// <para>
    /// Verhuisd uit <c>VeldOptimalisatie.razor.cs</c> (#1388): puur statisch, geen paginaspecifieke
    /// afhankelijkheid buiten de drempelwaarde, die de aanroeper meegeeft — zelfde opzet als de rest
    /// van deze klasse.
    /// </para>
    /// </summary>
    public static string GanttVoorkeurBalkKleur(int? afwijking, string? voorkeurTijd, int kleineAfwijkingDrempelMinuten)
    {
        if (voorkeurTijd == null || !afwijking.HasValue) return string.Empty;
        int abs = Math.Abs(afwijking.Value);
        if (abs == 0) return "var(--theme-voorkeur-op-tijd)";
        if (abs <= kleineAfwijkingDrempelMinuten) return "var(--theme-voorkeur-kleine-afwijking)";
        return "var(--theme-voorkeur-grote-afwijking)";
    }

    public static string GanttLabel(string label)
    {
        // Toon alleen de teamnamen, strip " - FC Onbekend JO7 1" achtervoegsel als het lang is
        var idx = label.IndexOf(" - ", StringComparison.Ordinal);
        if (idx > 0 && label.Length > 30) return label[..idx];
        return label;
    }

    /// <summary>Eerstvolgende zaterdag vanaf vandaag — de standaard-datum op zowel Planning als
    /// Veld optimalisatie. Als vandaag al zaterdag is, telt dat niet mee (dan is het antwoord vandaag
    /// over een week).</summary>
    public static DateOnly VolgendeZaterdag()
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        int dagenTotZaterdag = ((int)DayOfWeek.Saturday - (int)vandaag.DayOfWeek + 7) % 7;
        if (dagenTotZaterdag == 0) dagenTotZaterdag = 7;
        return vandaag.AddDays(dagenTotZaterdag);
    }
}
