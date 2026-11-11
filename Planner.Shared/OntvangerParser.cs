namespace Planner.Shared;

/// <summary>
/// Resultaat van <see cref="OntvangerParser.Parse"/>. Bij een ongeldige regel bevat
/// <see cref="FoutMelding"/> uitleg over de structuurfout of het ongeldige ontvanger-fragment.
/// Structuurfouten herhalen geen invoer; de hele regel wordt afgewezen, nooit deels overgeslagen.
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

        var fragmenten = SplitsOntvangers(ruweRegel, out var structuurGeldig);

        if (!structuurGeldig)
            return OntvangerParseResultaat.Ongeldig(
                "Ongeldig e-mailadres: controleer de aanhalingstekens en hoekhaken in de ontvangersregel.");

        if (fragmenten.Count == 0)
            return OntvangerParseResultaat.Ongeldig("Vul minimaal één ontvanger in.");

        var adressen = new List<string>();
        var gezien = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fragment in fragmenten)
        {
            if (!TryExtraheerAdres(fragment, out var adres))
                return OngeldigFragment(fragment);

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

    private static List<string> SplitsOntvangers(string regel, out bool structuurGeldig)
    {
        var fragmenten = new List<string>();
        var begin = 0;
        var tussenAanhalingstekens = false;
        var inHoekhaken = false;
        var geescapet = false;

        for (var i = 0; i < regel.Length; i++)
        {
            var teken = regel[i];

            if (tussenAanhalingstekens)
            {
                if (geescapet)
                {
                    geescapet = false;
                    continue;
                }

                if (teken == '\\')
                {
                    geescapet = true;
                    continue;
                }

                if (teken == '"')
                    tussenAanhalingstekens = false;

                continue;
            }

            if (teken == '"')
            {
                tussenAanhalingstekens = true;
                continue;
            }

            if (teken == '<')
            {
                if (inHoekhaken)
                    return OngeldigeStructuur(out structuurGeldig);

                inHoekhaken = true;
                continue;
            }

            if (teken == '>')
            {
                if (!inHoekhaken)
                    return OngeldigeStructuur(out structuurGeldig);

                inHoekhaken = false;
                continue;
            }

            if (!inHoekhaken && teken is ';' or ',')
            {
                VoegFragmentToe(fragmenten, regel[begin..i]);
                begin = i + 1;
            }
        }

        if (tussenAanhalingstekens || inHoekhaken)
            return OngeldigeStructuur(out structuurGeldig);

        VoegFragmentToe(fragmenten, regel[begin..]);
        structuurGeldig = true;
        return fragmenten;
    }

    private static List<string> OngeldigeStructuur(out bool structuurGeldig)
    {
        structuurGeldig = false;
        return [];
    }

    private static void VoegFragmentToe(List<string> fragmenten, string fragment)
    {
        var opgeschoond = fragment.Trim();
        if (opgeschoond.Length > 0)
            fragmenten.Add(opgeschoond);
    }

    private static OntvangerParseResultaat OngeldigFragment(string fragment) =>
        OntvangerParseResultaat.Ongeldig(
            $"Ongeldig e-mailadres: \"{fragment}\". Controleer of dit adres compleet is en " +
            "of de ontvangers gescheiden zijn door een puntkomma (;).");
}
