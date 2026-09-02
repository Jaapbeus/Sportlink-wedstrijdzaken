using System.Text.RegularExpressions;

namespace Planner.Shared;

/// <summary>
/// Onderdeel van de teamnaam→ID-vertaallaag (#692, #697). Deze klasse is de ENIGE plek waar
/// teamnaam-strings deterministisch (geen AI) genormaliseerd worden voor vergelijking/matching —
/// tier-agnostisch (#889), verhuisd uit <c>FunctionApp/TeamResolution/TeamNaamNormalisatie.cs</c>
/// naar hier zodat de Postgres-tier dezelfde, enige implementatie gebruikt in plaats van een
/// tweede kopie te bouwen — precies het architectuurrisico dat CLAUDE.md's regel "Normalisatieregels
/// horen uitsluitend in TeamNaamNormalisatie.cs" bedoelt te voorkomen. Woordelijk gelijk gedrag,
/// zelfde precedent als <see cref="VeldResolver"/> (#819).
///
/// <para>
/// <b>Waarom dit nodig is.</b> Sportlink levert per team twee schrijfwijzen aan, die naar
/// hetzelfde fysieke team verwijzen maar geen gedeelde sleutel hebben (<c>teamcode</c> is -1 bij
/// lokale teams, <c>lokaleteamcode</c> is -1 bij bondsteams). Geverifieerd tegen echte
/// <c>stg.teams</c>-data (#696):
/// </para>
/// <list type="table">
///   <item><description><c>teamsoort=lokaal</c> — clubeigen notatie: <c>JO10-1</c>, <c>MO13-1</c>, <c>G-1</c>, <c>1</c></description></item>
///   <item><description><c>teamsoort=bond</c> — KNVB-notatie mét clubprefix en ZONDER J: <c>[club] O10-1</c>, <c>[club] MO13-1</c>, <c>[club] G1</c>, <c>[club] 1</c></description></item>
/// </list>
/// <para>
/// De normalisatie brengt beide naar dezelfde sleutel, zodat één canoniek team overblijft.
/// Dit is ook precies waarom de oude <c>O13 → JO13</c>-regel bestond: geen e-mailtypfout, maar
/// het verschil tussen bonds- en lokale notatie.
/// </para>
///
/// <para>
/// Bewust NIET verantwoordelijk voor het raden van een ontbrekend geslacht-prefix (bijv. "13-1"
/// zonder JO/MO) — dat is een ambiguïteit die alleen met kandidaat-context oplosbaar is.
/// Zie <see cref="ITeamResolver"/> en <see cref="ITeamDisambiguator"/>.
/// </para>
/// </summary>
public static class TeamNaamNormalisatie
{
    // Woordelijke prefixen die met een spatie voor het nummer staan, bijv. "Onder 13".
    private static readonly (string Woord, string Vervanging)[] PrefixAliassen =
    [
        ("ONDER", "JO"),
        ("MEISJES", "MO"),
        ("VROUWEN", "VR"),
        ("DAMES", "VR"),
    ];

    /// <summary>
    /// Genormaliseerde sleutel voor exacte vergelijking/lookup: uppercase, geen spaties,
    /// consistente scheidingstekens, zonder clubprefix. Twee teksten die hetzelfde team bedoelen
    /// leveren dezelfde sleutel op.
    /// </summary>
    /// <param name="clubPrefix">
    /// De ClubCode van de eigen club (uit <c>dbo.AppSettings</c>, nooit hardcoded). Wanneer
    /// opgegeven wordt een leidend clubprefix verwijderd, zodat de KNVB-notatie
    /// ("[club] O10-1") samenvalt met de lokale notatie ("JO10-1"). Laat <c>null</c> voor een
    /// tegenstander-teamnaam: daar is het clubdeel juist onderscheidend.
    /// </param>
    public static string NormaliseerVoorVergelijking(string? ruweTekst, string? clubPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(ruweTekst)) return "";

        var t = ruweTekst.Trim();

        // Seizoensaanduidingen/toevoegingen tussen haakjes strippen, bijv. "JO13-2 (2025-2026)".
        t = Regex.Replace(t, @"\s*\(.*?\)\s*", " ").Trim();

        t = StripClubPrefix(t, clubPrefix);

        // Cijfer-0 i.p.v. letter-O in het prefix, bijv. "J013-2" → "JO13-2".
        t = Regex.Replace(t, @"\bJ0(?=\d)", "JO", RegexOptions.IgnoreCase);

        // Regionale volgorde-variant, direct tegen het nummer geplakt: "MJ13-1" → "JM13-1".
        t = Regex.Replace(t, @"\bMJ(?=\d)", "JM", RegexOptions.IgnoreCase);

        foreach (var (woord, vervanging) in PrefixAliassen)
            t = Regex.Replace(t, $@"\b{woord}\b\.?\s*", vervanging, RegexOptions.IgnoreCase);

        // KNVB-notatie naar lokale notatie: "O10-1" → "JO10-1". De lookbehind voorkomt dat dit
        // ook "MO10-1" of een al genormaliseerd "JO10-1" raakt (daar staat een letter vóór de O).
        t = Regex.Replace(t, @"(?<![A-Za-z])O(?=\d)", "JO", RegexOptions.IgnoreCase);

        // Scheidingstekens tussen twee cijferreeksen naar één streepje, ongeacht
        // spatie/slash/punt/komma: "JO13 / 2", "JO13.2", "JO13 - 2" → "JO13-2".
        // '+' blijft bewust ongemoeid: dat hoort bij veteranenteams ("35+1", "VR30+1").
        t = Regex.Replace(t, @"(\d)\s*[/,.\-]\s*(\d)", "$1-$2");

        // Een kále spatie tussen leeftijd en teamnummer is óók een scheidingsteken:
        // "MO13 1" → "MO13-1". Zonder deze regel viel die vorm in de generieke
        // whitespace-strip hieronder en werd de sleutel "MO131" — een andere sleutel dan
        // "MO13-1", terwijl het hetzelfde team is. Dat brak de teamherkenning volledig voor
        // elke bron die deze notatie gebruikt (#766).
        t = Regex.Replace(t, @"(\d)\s+(\d)", "$1-$2");

        // Streepje tussen een letter-only categorie en een teamnummer collapsen: "G-1" → "G1"
        // (lokale notatie) zodat het samenvalt met de bondsnotatie "G1". Een categorie MET cijfers
        // ("JO13-1") houdt zijn streepje — daar scheidt het de leeftijd van het teamnummer.
        t = Regex.Replace(t, @"(?<![A-Za-z0-9])([A-Za-z]+)-(?=\d)", "$1", RegexOptions.IgnoreCase);

        t = Regex.Replace(t, @"\s+", "");

        return t.ToUpperInvariant();
    }

    private static string StripClubPrefix(string tekst, string? clubPrefix)
    {
        if (string.IsNullOrWhiteSpace(clubPrefix)) return tekst;

        var prefix = clubPrefix.Trim();
        if (!tekst.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return tekst;

        var rest = tekst[prefix.Length..].TrimStart(' ', '-', '_');

        // Alleen strippen als er daadwerkelijk een teamaanduiding overblijft. "VRC" alleen mag
        // nooit tot een lege sleutel leiden.
        return rest.Length == 0 ? tekst : rest;
    }

    /// <summary>
    /// True als de tekst qua vorm op een teamaanduiding lijkt. Gebruikt om vrije tekst
    /// ("Kan de wedstrijd verplaatst worden?") te onderscheiden van een teamverwijzing.
    /// </summary>
    public static bool LijktOpTeamPatroon(string? ruweTekst, string? clubPrefix = null)
    {
        var key = NormaliseerVoorVergelijking(ruweTekst, clubPrefix);
        if (key.Length == 0) return false;

        // Dekt: JO13-2, MO13-1, 13-1, G1, VR1, 1, 35+1, VR30+1, JO14-1JM
        return Regex.IsMatch(key, @"^(JO|MO|VR|JM|ZO|G)?\d+(\+\d+)?(-\d+)?(JM)?$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Ontleedt een leeftijdscategorie-teamnummer-combinatie ("JO13-2", "13-1") in componenten,
    /// voor de kandidatenzoektocht bij een ontbrekend geslacht-prefix.
    /// </summary>
    /// <remarks>
    /// Geeft bewust <c>null</c> voor teamvormen waar "leeftijd + teamnummer" niet bestaat:
    /// senioren ("1"), veteranen ("35+1"), vrouwen ("VR1"), G-teams ("G1") en teams met een
    /// eigen naam ("Spitsies"). Voor die vormen is alleen een exacte match of een gevalideerde
    /// alias correct — kandidaten zoeken op nummer zou daar juist verkeerde treffers geven.
    /// </remarks>
    public static TeamNaamComponenten? Parse(string? ruweTekst, string? clubPrefix = null)
    {
        var key = NormaliseerVoorVergelijking(ruweTekst, clubPrefix);
        if (key.Length == 0) return null;

        var match = Regex.Match(key, @"^(?<prefix>JO|MO)?(?<leeftijd>\d{1,2})-(?<team>\d{1,2})$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        string? prefix = match.Groups["prefix"].Success ? match.Groups["prefix"].Value.ToUpperInvariant() : null;

        return new TeamNaamComponenten(
            prefix,
            int.Parse(match.Groups["leeftijd"].Value),
            int.Parse(match.Groups["team"].Value),
            key);
    }
}

/// <summary>Ontlede onderdelen van een genormaliseerde teamnaam. Zie <see cref="TeamNaamNormalisatie.Parse"/>.</summary>
public sealed record TeamNaamComponenten(string? Prefix, int? LeeftijdNummer, int? TeamNummer, string GenormaliseerdeSleutel);
