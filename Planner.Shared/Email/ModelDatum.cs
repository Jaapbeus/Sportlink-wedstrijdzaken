using System.Globalization;

namespace Planner.Shared.Email;

/// <summary>
/// Strikte validatie van datums die uit vrije modeltekst komen (#1730). Alleen de vorm <c>jjjj-MM-dd</c>
/// van een bestaande kalenderdatum is geldig; alles anders valt weg, zodat ongeldige modeltekst nooit
/// letterlijk in een automatisch antwoord terechtkomt.
/// </summary>
public static class ModelDatum
{
    private const string Vorm = "yyyy-MM-dd";

    /// <summary>Normaliseert één waarde naar <c>jjjj-MM-dd</c>; <c>null</c> als de waarde geen strikte datum is.</summary>
    public static string? Normaliseer(string? waarde)
        => DateOnly.TryParseExact(waarde?.Trim(), Vorm, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString(Vorm, CultureInfo.InvariantCulture)
            : null;

    /// <summary>De geldige, unieke datums uit de lijst in de oorspronkelijke volgorde; ongeldige waarden vallen af.</summary>
    public static List<string> Normaliseer(IEnumerable<string?> waarden)
        => waarden.Select(Normaliseer).OfType<string>().Distinct().ToList();

    /// <summary>
    /// De datums waarover een beschikbaarheidscheck gaat: de (eventueel uitgebreide) datumlijst van het
    /// model, of de losse modeldatum als die lijst leeg is — strikt gevalideerd en uniek, in volgorde.
    /// Een lijst met uitsluitend ongeldige waarden valt NIET terug op de losse datum: dan zou de
    /// plannercheck over een andere datum lopen dan de tegenstander-lookup. Leeg betekent "datum onbekend".
    /// </summary>
    public static List<string> VoorBeschikbaarheid(IReadOnlyList<string> alleDatums, string? modelDatum)
        => Normaliseer(alleDatums.Count > 0 ? alleDatums : new[] { modelDatum });
}
