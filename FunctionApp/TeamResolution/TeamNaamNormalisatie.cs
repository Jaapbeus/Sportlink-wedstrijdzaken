using System.Text.RegularExpressions;

namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Onderdeel van de teamnaam→ID-vertaallaag (#692, #697). Deze klasse is de ENIGE plek waar
/// teamnaam-strings deterministisch (geen AI) genormaliseerd worden voor vergelijking/matching.
/// Vervangt op termijn de losse regex in <c>BerichtPipeline.NormaliseerTeamNaam</c>/
/// <c>NormaliseerLeeftijdsCategorie</c> (nog niet verwijderd — zie #700, pas na shadow-mode-validatie
/// in #698).
///
/// Bewust NIET verantwoordelijk voor:
/// - het raden van een ontbrekend geslacht-prefix (bijv. "13-1" zonder JO/MO) — dat is een
///   ambiguïteit die alleen met extra context (kandidatenlijst, evt. AI-disambiguatie) opgelost
///   kan worden, niet met een pure string-functie. Zie <see cref="ITeamResolver"/>.
/// - het toevoegen van een club-prefix (dat deed <c>NormaliseerTeamNaam</c> wel) — canonieke
///   identiteit hoort in <c>dbo.Teams</c>/<c>dbo.TeamAliassen</c> te leven, niet in de string zelf.
/// </summary>
public static class TeamNaamNormalisatie
{
    // Woordelijke prefixen die typisch met een spatie voor het nummer staan, bijv. "Onder 13".
    private static readonly Dictionary<string, string> PrefixAliassen = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ONDER"] = "JO",
        ["MEISJES"] = "MO",
        ["VROUWEN"] = "VR",
        ["DAMES"] = "VR",
        ["ZAAL"] = "ZO",
    };

    /// <summary>
    /// Genormaliseerde sleutel voor exacte vergelijking/lookup: uppercase, geen spaties,
    /// consistente scheidingstekens. Twee teksten die hetzelfde team bedoelen (ook met
    /// afwijkende spatiëring/streepjes/hoofdletters) leveren dezelfde sleutel op.
    /// </summary>
    public static string NormaliseerVoorVergelijking(string? ruweTekst)
    {
        if (string.IsNullOrWhiteSpace(ruweTekst)) return "";

        var t = ruweTekst.Trim();

        // Strip seizoensaanduidingen/haakjes-toevoegingen, bijv. "JO13-2 (2025-2026)" (#692 scenario 21)
        t = Regex.Replace(t, @"\s*\(.*?\)\s*", " ").Trim();

        // Cijfer-0/letter-O typefout in prefix, bijv. "J013-2" → "JO13-2" (#692 scenario 10)
        t = Regex.Replace(t, @"\bJ0(\d)", "JO$1", RegexOptions.IgnoreCase);

        // Regionale volgorde-variant, direct tegen het nummer geplakt: "MJ13-1" → "JM13-1"
        // (#692 scenario 14). Geen \b na de prefix: die staat hier nooit los van het nummer.
        t = Regex.Replace(t, @"\bMJ(?=\d)", "JM", RegexOptions.IgnoreCase);

        // Woordelijke prefixen naar korte vorm, bijv. "Onder 13" → "JO13"
        foreach (var (woord, vervanging) in PrefixAliassen)
        {
            t = Regex.Replace(t, $@"\b{woord}\b\.?\s*", vervanging, RegexOptions.IgnoreCase);
        }

        // "O13"/"o13" → "JO13". De negative lookbehind voorkomt dat dit ook "MO13"/"JO13"
        // raakt: daar wordt de O al voorafgegaan door een letter (#692 scenario 2).
        t = Regex.Replace(t, @"(?<![A-Za-z])O(\d)", "JO$1", RegexOptions.IgnoreCase);

        // Scheidingstekens tussen twee cijferreeksen normaliseren naar één streepje,
        // ongeacht spatie/slash/punt/komma (#692 scenario's 1, 3, 11, 19)
        t = Regex.Replace(t, @"(\d)\s*[/,.\-]\s*(\d)", "$1-$2");

        // Alle overige spaties verwijderen (na scheidingsteken-normalisatie, dus "JO 13 - 2" → "JO13-2")
        t = Regex.Replace(t, @"\s+", "");

        return t.ToUpperInvariant();
    }

    /// <summary>
    /// True als de (genormaliseerde) tekst qua vorm op een teamnaam lijkt — een bekend
    /// prefix gevolgd door een leeftijds-/teamnummer, of een kale nummer-reeks zoals "13-1".
    /// Gebruikt om te onderscheiden van evident niet-team-gerelateerde tekst (#692 scenario 24).
    /// </summary>
    public static bool LijktOpTeamPatroon(string? ruweTekst)
    {
        var key = NormaliseerVoorVergelijking(ruweTekst);
        if (key.Length == 0) return false;
        return Regex.IsMatch(key, @"^(JO|MO|VR|JM|ZO|G)?\d+(-\d+)?$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Ontleedt een genormaliseerde teamnaam in prefix/leeftijdnummer/teamnummer, voor zover
    /// aanwezig. Geeft <c>null</c> voor componenten die niet uit de tekst zijn af te leiden —
    /// met name <see cref="TeamNaamComponenten.Prefix"/> ontbreekt bewust bij kale nummers
    /// zoals "13-1" (#692 scenario 4): dat is een ambiguïteit, geen normalisatiefout.
    /// </summary>
    public static TeamNaamComponenten? Parse(string? ruweTekst)
    {
        var key = NormaliseerVoorVergelijking(ruweTekst);
        if (key.Length == 0) return null;

        var match = Regex.Match(key, @"^(?<prefix>JO|MO|VR|JM|ZO|G)?(?<leeftijd>\d+)?(?:-(?<team>\d+))?$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        string? prefix = match.Groups["prefix"].Success ? match.Groups["prefix"].Value.ToUpperInvariant() : null;
        int? leeftijd = match.Groups["leeftijd"].Success && int.TryParse(match.Groups["leeftijd"].Value, out var l) ? l : null;
        int? teamNummer = match.Groups["team"].Success && int.TryParse(match.Groups["team"].Value, out var tn) ? tn : null;

        if (prefix == null && leeftijd == null && teamNummer == null) return null;

        return new TeamNaamComponenten(prefix, leeftijd, teamNummer, key);
    }
}

/// <summary>Ontlede onderdelen van een genormaliseerde teamnaam. Zie <see cref="TeamNaamNormalisatie.Parse"/>.</summary>
public sealed record TeamNaamComponenten(string? Prefix, int? LeeftijdNummer, int? TeamNummer, string GenormaliseerdeSleutel);
