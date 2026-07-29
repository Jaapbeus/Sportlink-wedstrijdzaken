namespace SportlinkFunction.Utilities;

/// <summary>
/// Resultaat van <see cref="OntvangerParser.Parse"/>. Bij een ongeldige regel bevat
/// <see cref="FoutMelding"/> altijd welk exact ontvanger-fragment de fout veroorzaakte — een stille
/// skip zou de aanvrager laten denken dat een adres wél is meegenomen terwijl dat niet zo is.
/// </summary>
public sealed record OntvangerParseResultaat(bool IsValid, IReadOnlyList<string> Emailadressen, string? FoutMelding)
{
    public static OntvangerParseResultaat Ongeldig(string foutMelding) => new(false, [], foutMelding);
    public static OntvangerParseResultaat Geldig(IReadOnlyList<string> emailadressen) => new(true, emailadressen, null);
}

/// <summary>
/// Enige plek waar de vrije-tekst-regel uit het "Email Aan"-veld (#765) wordt omgezet naar een lijst
/// geldige, unieke e-mailadressen. Hoort bewust los van <c>AdminTeambegeleidingFunction</c>: dit is
/// precies het soort string-verwerking dat je één keer goed doet, unit test en daarna nooit meer
/// aanraakt.
/// </summary>
public static class OntvangerParser
{
    public const int MaxAantalOntvangers = 15;

    /// <summary>
    /// Parseert een regel met één of meer ontvangers, gescheiden door <c>;</c> of <c>,</c>. Elk
    /// fragment mag zowel <c>"Naam" &lt;adres&gt;</c> als een kaal adres zijn —
    /// <see cref="System.Net.Mail.MailAddress"/> ontleedt beide vormen. Dubbele adressen (ongeacht
    /// hoofdlettergebruik) worden stilzwijgend samengevoegd tot één ontvanger.
    /// </summary>
    public static OntvangerParseResultaat Parse(string? ruweRegel)
    {
        if (string.IsNullOrWhiteSpace(ruweRegel))
            return OntvangerParseResultaat.Ongeldig("Vul minimaal één ontvanger in.");

        var fragmenten = ruweRegel
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => f.Length > 0)
            .ToList();

        if (fragmenten.Count == 0)
            return OntvangerParseResultaat.Ongeldig("Vul minimaal één ontvanger in.");

        var adressen = new List<string>();
        var gezien = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fragment in fragmenten)
        {
            if (!TryExtraheerAdres(fragment, out var adres))
                return OntvangerParseResultaat.Ongeldig(
                    $"Ongeldig e-mailadres: \"{fragment}\". Controleer of dit adres compleet is en " +
                    "of de ontvangers gescheiden zijn door een puntkomma (;).");

            if (gezien.Add(adres))
                adressen.Add(adres);
        }

        if (adressen.Count > MaxAantalOntvangers)
            return OntvangerParseResultaat.Ongeldig(
                $"Maximaal {MaxAantalOntvangers} ontvangers per verzending (opgegeven: {adressen.Count}).");

        return OntvangerParseResultaat.Geldig(adressen);
    }

    private static bool TryExtraheerAdres(string fragment, out string adres)
    {
        try
        {
            // MailAddress ontleedt zowel "Naam" <adres> als een kaal adres uit één string —
            // een eigen regex voor het display-name-gedeelte is dus overbodig.
            adres = new System.Net.Mail.MailAddress(fragment).Address;
            return true;
        }
        catch (FormatException)
        {
            adres = "";
            return false;
        }
    }
}
