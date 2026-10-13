using System.Globalization;

namespace Planner.Shared.Email;

/// <summary>
/// De ene strikte grens voor datums die uit vrije modeltekst komen (#1730). Alleen de vorm
/// <c>jjjj-MM-dd</c> van een bestaande kalenderdatum is geldig; alles anders valt weg, zodat ongeldige
/// modeltekst nooit letterlijk in een automatisch antwoord, een query of een trace terechtkomt. Beide
/// tiers passen <see cref="Grens"/> toe op de classificatie vóór enig gebruik van een datumveld en
/// lezen een datum daarna uitsluitend via <see cref="TryParse"/>; een lenige <c>DateOnly.TryParse</c>
/// (cultuurafhankelijk) komt op modelvelden niet meer voor.
/// </summary>
public static class ModelDatum
{
    private const string Vorm = "yyyy-MM-dd";

    /// <summary>Strikte lezing van één waarde; <c>false</c> voor elke andere vorm, ook een andere cultuur.</summary>
    public static bool TryParse(string? waarde, out DateOnly datum)
        => DateOnly.TryParseExact(waarde?.Trim(), Vorm, CultureInfo.InvariantCulture, DateTimeStyles.None, out datum);

    /// <summary>Normaliseert één waarde naar <c>jjjj-MM-dd</c>; <c>null</c> als de waarde geen strikte datum is.</summary>
    public static string? Normaliseer(string? waarde)
        => TryParse(waarde, out var d) ? d.ToString(Vorm, CultureInfo.InvariantCulture) : null;

    /// <summary>De geldige, unieke datums uit de lijst in de oorspronkelijke volgorde; ongeldige waarden vallen af.</summary>
    public static List<string> Normaliseer(IEnumerable<string?> waarden)
        => waarden.Select(Normaliseer).OfType<string>().Distinct().ToList();

    /// <summary>
    /// De grens voor alle datumvelden van een classificatie: elk veld komt strikt genormaliseerd terug
    /// of valt weg (<c>null</c>; een lijst zonder geldige datum wordt <c>null</c>). Wordt in beide tiers
    /// eenmalig toegepast vóór trace, teamherkenning en verwerking, zodat elk pad daarna dezelfde
    /// gevalideerde waarden ziet.
    /// </summary>
    public static ModelDatumVelden Grens(string? datum, string? gewensteDatum, IEnumerable<string?>? datums)
    {
        var lijst = datums is null ? null : Normaliseer(datums);
        return new(Normaliseer(datum), Normaliseer(gewensteDatum), lijst is { Count: > 0 } ? lijst : null);
    }

    /// <summary>
    /// De datums waarover een beschikbaarheidscheck gaat: de (eventueel uitgebreide) datumlijst van het
    /// model, of de losse modeldatum als die lijst leeg is — strikt gevalideerd en uniek, in volgorde.
    /// Een lijst met uitsluitend ongeldige waarden valt NIET terug op de losse datum: dan zou de
    /// plannercheck over een andere datum lopen dan de tegenstander-lookup (na <see cref="Grens"/> komt
    /// zo'n lijst niet meer voor; de invariant blijft hier staan). Leeg betekent "datum onbekend".
    /// </summary>
    public static List<string> VoorBeschikbaarheid(IReadOnlyList<string> alleDatums, string? modelDatum)
        => Normaliseer(alleDatums.Count > 0 ? alleDatums : new[] { modelDatum });
}

/// <summary>De door <see cref="ModelDatum.Grens"/> gevalideerde datumvelden van een classificatie.</summary>
public sealed record ModelDatumVelden(string? Datum, string? GewensteDatum, List<string>? Datums);
