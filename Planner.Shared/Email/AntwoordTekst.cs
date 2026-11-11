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
    {
        var naam = AiVeldSanering.AfzenderNaamWeergave(volledigeNaam);
        return naam?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    /// <summary>Aanhef met veilige voornaam, of alleen de aanhef als de naam ontbreekt.</summary>
    public static string AanhefMetNaam(string aanhef, string? volledigeNaam)
        => $"{aanhef} {Voornaam(volledigeNaam)}".TrimEnd();

    /// <summary>Oorspronkelijk adres voor de interne review: één begrensd, escaped tekstveld.</summary>
    public static string ReviewAfzenderAdres(string? adres)
    {
        if (string.IsNullOrWhiteSpace(adres) || adres.Length > 254 || adres.Any(char.IsControl))
            return "adres niet beschikbaar";
        return System.Net.WebUtility.HtmlEncode(adres);
    }

    /// <summary>Onderwerp en tekst voor de interne teamleider-notificatie, gedeeld door beide tiers.</summary>
    public static (string Onderwerp, string Inhoud) TeamleiderNotificatie(
        string teamNaam, string datum, string teamleiderNaam, string plannerNaam)
    {
        var team = AiVeldSanering.TeamNaamOfGeneriek(teamNaam, onbevestigd: false);
        var datumTekst = Datum(datum);
        var inhoud = $"Hoi {teamleiderNaam},\n\n"
            + $"Er is een herplanverzoek ontvangen voor {team} op {datumTekst}.\n\n"
            + "De coördinator heeft automatisch gereageerd op dit verzoek. "
            + "Je hoeft zelf geen actie te ondernemen, maar we willen je op de hoogte houden.\n\n"
            + "Als je vragen hebt over dit herplanverzoek, neem dan contact op met de veldplanner.\n\n"
            + $"Met vriendelijke groet,\n{plannerNaam}";
        return ($"Herplanverzoek ontvangen voor {team} op {datumTekst}", inhoud);
    }

    /// <summary>Veilig onderwerp voor uitgaande reply en reviewkop; ongeldige invoer wordt generiek.</summary>
    public static string Onderwerp(string? onderwerp)
        => AiVeldSanering.OnderwerpWeergave(onderwerp) ?? "bericht";

    /// <summary>Datum als "zaterdag 10 oktober 2026"; een niet-leesbare tekst komt nooit letterlijk terug (#1730).</summary>
    public static string Datum(string? datumString)
        => DateOnly.TryParse(datumString, out var datum)
            ? datum.ToString("dddd d MMMM yyyy", NL)
            : "de opgegeven datum";

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
