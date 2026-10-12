using System.Globalization;

namespace Planner.Shared.Email;

/// <summary>
/// Tier-onafhankelijke tekstbouwstenen van de automatische antwoorden: aanhef, voornaam, datumnotatie en
/// de selectie van alternatieven. Zonder database of configuratie, dus los te testen.
/// </summary>
public static class AntwoordTekst
{
    private static readonly CultureInfo NL = new("nl-NL");
    private const string NlTijdzoneId = "W. Europe Standard Time";

    /// <summary>Aanhef op basis van het uur in Nederland op het gegeven UTC-moment.</summary>
    public static string Aanhef(DateTime utcNu)
    {
        var uur = TimeZoneInfo.ConvertTimeFromUtc(utcNu, TimeZoneInfo.FindSystemTimeZoneById(NlTijdzoneId)).Hour;
        if (uur < 12) return "Goedemorgen";
        if (uur < 18) return "Goedemiddag";
        return "Goedenavond";
    }

    /// <summary>Eerste woord van een volledige naam, of leeg.</summary>
    public static string Voornaam(string? volledigeNaam)
        => string.IsNullOrWhiteSpace(volledigeNaam)
            ? ""
            : volledigeNaam.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>Datum als "zaterdag 10 oktober 2026"; een niet-leesbare tekst komt ongewijzigd terug.</summary>
    public static string Datum(string? datumString)
        => DateOnly.TryParse(datumString, out var datum)
            ? datum.ToString("dddd d MMMM yyyy", NL)
            : datumString ?? "de opgegeven datum";

    /// <summary>
    /// Beperkt de alternatieven tot kunstgras zodra er drie of meer kunstgrasvelden beschikbaar zijn:
    /// op zo'n ruime dag hoeft het antwoord geen natuurgras aan te bieden. Alleen aantoonbaar natuurgras
    /// verdwijnt (#707): een veldtype dat noch kunstgras noch natuurgras is, telt niet mee voor de
    /// drempel en wordt nooit weggefilterd. Het veldtype komt uit de velden van de club (#705), niet
    /// uit het veldnummer.
    /// </summary>
    public static List<SlotToewijzing> FilterAlternatieven(List<SlotToewijzing> alternatieven)
    {
        var kunstgrasVelden = alternatieven
            .Where(a => VeldTypeClassificatie.IsKunstgras(a.VeldType))
            .Select(a => a.VeldNummer).Distinct().Count();
        if (kunstgrasVelden < 3)
            return alternatieven;

        // Bewust IsNatuurgras en niet !IsKunstgras: onbekend is geen natuurgras en blijft staan.
        return alternatieven
            .Where(a => !VeldTypeClassificatie.IsNatuurgras(a.VeldType))
            .ToList();
    }
}
