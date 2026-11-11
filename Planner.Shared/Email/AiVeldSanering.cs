using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>
/// De ene plek die bepaalt welke vrije modelvelden (KNVB-notitie, teamteksten, aanvangstijd) in
/// een antwoord op een inkomend bericht mogen belanden. Pure functies, geen databasetype: beide
/// tiers roepen ze aan op de classificatie voordat een veld in tekst wordt gebruikt.
/// </summary>
public static partial class AiVeldSanering
{
    public const int MaxKnvbNotitieLengte = 200;
    public const int MaxTeamNaamLengte = 40;
    public const int MaxTegenstanderLengte = 60;
    public const int MaxSamenvattingLengte = 500;
    public const int MaxOnderwerpLengte = 200;

    /// <summary>Generieke tekst in een antwoord als de teamnaam niet getoond mag worden.</summary>
    public const string GeneriekTeam = "het opgegeven team";

    /// <summary>Generieke tekst in een antwoord als de tegenstander niet getoond mag worden.</summary>
    public const string GeneriekeTegenstander = "de opgegeven tegenstander";

    private const string AanvangsTijdFormaat = "HH:mm";

    // Een domeinnaam: een label van minstens twee tekens, een punt en minstens twee letters
    // ("voorbeeld.nl"), of een label van één teken dat niet zelf achter een punt staat, gevolgd door
    // een kleine-letter-extensie ("t.co"). Puntafkortingen zonder spatie ("v.v.Voorbeeld", "S.V.")
    // hebben alleen labels van één teken en tellen daardoor niet als domein.
    [GeneratedRegex(@"[\p{L}\p{N}\-]{2,}\.\p{L}{2,}|(?<![\p{L}\p{N}.\-])[\p{L}\p{N}]\.\p{Ll}{2,}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex DomeinPatroon();

    // Naamvorm van een club of team: optioneel een apostrof vooraan ("'t Voorbeeld", "'s-Voorbeeld"),
    // dan een letter of cijfer, daarna letters, cijfers, spatie en . + / - ' ’ & ( ) ,
    [GeneratedRegex(@"^['’]?[\p{L}\p{N}][\p{L}\p{N} .+/\-'’&(),]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NaamPatroon();

    /// <summary>
    /// KNVB-notitie uit het model: korte platte tekst zonder URL, markup of regeleinden. Alles wat daar
    /// niet aan voldoet (ook te lang) vervalt volledig; er wordt nooit ingekort of herschreven.
    /// </summary>
    public static string? KnvbNotitie(string? waarde)
    {
        var tekst = NormaliseerWitruimte(waarde);
        if (tekst.Length == 0 || tekst.Length > MaxKnvbNotitieLengte)
            return null;

        return BevatLinkOfMarkup(tekst) ? null : tekst;
    }

    /// <summary><see cref="KnvbNotitie(string?)"/>, met een logregel (zonder inhoud) als een ingevulde waarde vervalt.</summary>
    public static string? KnvbNotitie(string? waarde, ILogger? log)
        => LogAlsVervallen(waarde, KnvbNotitie(waarde), "knvbNotitie", log);

    /// <summary><see cref="AanvangsTijd(string?)"/>, met een logregel (zonder inhoud) als een ingevulde waarde vervalt.</summary>
    public static string? AanvangsTijd(string? waarde, ILogger? log)
        => LogAlsVervallen(waarde, AanvangsTijd(waarde), "aanvangsTijd", log);

    /// <summary>Aanvangstijd uit het model als <c>HH:mm</c>; alles wat daar niet strikt op lijkt vervalt.</summary>
    public static string? AanvangsTijd(string? waarde)
    {
        var tekst = (waarde ?? "").Trim();
        if (tekst.Length == 0) return null;

        if (!TimeOnly.TryParseExact(tekst, [AanvangsTijdFormaat, "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var tijd))
            return null;

        return tijd.ToString(AanvangsTijdFormaat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Teamnaam zoals die in een antwoord mag staan. Alleen een door de teamresolutie bevestigde naam
    /// (<paramref name="onbevestigd"/> = false) die ook de naamvorm heeft komt door; anders <c>null</c>
    /// en gebruikt de aanroeper generieke tekst. Een ontbrekende teamnaam wordt nooit geraden.
    /// </summary>
    public static string? TeamNaamWeergave(string? waarde, bool onbevestigd)
        => onbevestigd ? null : Weergavenaam(waarde, MaxTeamNaamLengte);

    /// <summary>
    /// Teamnaam voor lopende tekst en sjablonen: de bevestigde, veilige naam, <paramref name="generiek"/> als er
    /// wel een waarde is die niet getoond mag worden, en leeg als er niets is ingevuld.
    /// </summary>
    public static string TeamNaamOfGeneriek(string? waarde, bool onbevestigd, string generiek = GeneriekTeam)
        => TeamNaamWeergave(waarde, onbevestigd) ?? (string.IsNullOrWhiteSpace(waarde) ? "" : generiek);

    /// <summary>Tegenstander zoals die in een antwoord mag staan: korte naamvorm zonder URL of markup, anders <c>null</c>.</summary>
    public static string? TegenstanderWeergave(string? waarde) => Weergavenaam(waarde, MaxTegenstanderLengte);

    /// <summary>
    /// Tegenstander voor lopende tekst: de veilige naam, <paramref name="generiek"/> als er wel een waarde is die niet
    /// door de sanering komt, en de ongewijzigde lege waarde als er niets is ingevuld.
    /// </summary>
    public static string TegenstanderOfGeneriek(string? waarde, string generiek = GeneriekeTegenstander)
        => TegenstanderWeergave(waarde) ?? (string.IsNullOrWhiteSpace(waarde) ? waarde ?? "" : generiek);

    /// <summary>Een afzendernaam die veilig als aanhef of koptekst kan worden getoond; ongeldige namen vervallen.</summary>
    public static string? AfzenderNaamWeergave(string? waarde) => Weergavenaam(waarde, 80);

    /// <summary>Onderwerpregel voor een uitgaand bericht: één veilige, begrensde regel of een generieke fallback.</summary>
    public static string? OnderwerpWeergave(string? waarde)
    {
        if (waarde is null || waarde.Any(char.IsControl)) return null;
        var tekst = NormaliseerWitruimte(waarde);
        return tekst.Length is > 0 and <= MaxOnderwerpLengte && !BevatLinkOfMarkup(tekst) ? tekst : null;
    }

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPatroon();

    // Preserve whole dates, but do not consume the first groups of a longer dotted/dashed number.
    [GeneratedRegex(@"(?<datum>\b(?:\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4})\b(?![-/.]\d))|(?<![\p{L}\p{N}])(?<nummer>\+?\(?\d(?:[().\p{Zs}\-]*\d){8,14})(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex NummerPatroon();

    // Provider summaries keep explicit planning dates and clock times, while masking every other
    // long digit sequence, including values from persisted examples that predate current validation.
    [GeneratedRegex(@"(?<datum>\b(?:\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4})\b(?![-/.]\d))|(?<tijd>\b(?:[01]?\d|2[0-3])[:.][0-5]\d\b)|(?<nummer>(?<![\p{L}\p{N}])\+?\d(?:[\p{Zs}.\-]?\d){8,}(?![\p{L}\p{N}]))", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderNummerPatroon();

    /// <summary>Maskeert contactpatronen voordat externe berichttekst naar de AI-provider gaat.</summary>
    public static string MaskeerContactgegevens(string? tekst)
    {
        if (string.IsNullOrEmpty(tekst)) return tekst ?? "";
        return NummerPatroon().Replace(EmailPatroon().Replace(tekst, "[e-mail]"), match =>
        {
            if (match.Groups["datum"].Success) return match.Value;
            var nummer = match.Value;
            var cijfers = new string(nummer.Where(char.IsDigit).ToArray());
            // The 00 dialing prefix is followed by country-code digits, optionally after spaces.
            // A dot immediately after 00 instead belongs to a clock time, such as a midnight time list.
            var internationaal = nummer.StartsWith('+')
                || (cijfers.Length >= 11 && nummer.StartsWith("00", StringComparison.Ordinal)
                    && char.IsDigit(nummer[2..].TrimStart()[0]));
            var nationaal = nummer.TrimStart('(').StartsWith('0') && cijfers.Length == 10;
            return internationaal || nationaal ? "[nummer]" : nummer;
        });
    }

    /// <summary>Korte samenvatting uit het model: één regel, begrensd, zonder aanhalingstekens die een prompt kunnen doorbreken.</summary>
    public static string Samenvatting(string? waarde)
        => ZonderDubbeleAanhalingstekens(Trace.TraceBuilder.Saneer(waarde, MaxSamenvattingLengte));

    /// <summary>
    /// Samenvatting uitsluitend voor providerinvoer: behoudt expliciete datums en tijden, maskeert
    /// lange nummerreeksen en herkenbare contactpatronen, en hergebruikt de normalisatie en lengtekap.
    /// Opslag/trace houden de striktere <see cref="Samenvatting"/>; reeds gemaskeerde opgeslagen inhoud
    /// wordt niet gereconstrueerd.
    /// </summary>
    public static string ProviderSamenvatting(string? waarde)
    {
        var schoon = ProviderNummerPatroon().Replace(MaskeerContactgegevens(waarde), match =>
            match.Groups["datum"].Success || match.Groups["tijd"].Success ? match.Value : "[nummer]");
        schoon = NormaliseerWitruimte(schoon);
        if (schoon.Length > MaxSamenvattingLengte) schoon = schoon[..MaxSamenvattingLengte];
        return ZonderDubbeleAanhalingstekens(schoon);
    }

    private static string ZonderDubbeleAanhalingstekens(string tekst) => tekst.Replace('"', '\'');

    /// <summary><see cref="Samenvatting"/>, maar <c>null</c> als er na het saneren niets overblijft.</summary>
    public static string? SamenvattingOfNull(string? waarde)
    {
        var schoon = Samenvatting(waarde);
        return schoon.Length == 0 ? null : schoon;
    }

    public const int MaxWaarschuwingLengte = 300;

    /// <summary>
    /// Waarschuwingstekst voor in een antwoord (#1809): één regel, begrensd, zonder markup, en zonder woorden die een
    /// link of e-mailadres vormen. Bedoeld als laatste vangnet op de uiteindelijke uitvoer, ook als een deel van de
    /// tekst uit modeluitvoer komt. Anders dan de naamvelden wordt er niet geweigerd maar gezuiverd, zodat een
    /// legitieme planningswaarschuwing niet verdwijnt.
    /// </summary>
    public static string Waarschuwing(string? waarde)
    {
        var tekst = NormaliseerWitruimte(waarde);
        if (tekst.Length == 0) return "";

        var woorden = tekst
            .Replace('<', ' ').Replace('>', ' ').Replace('`', ' ').Replace('{', ' ').Replace('}', ' ')
            .Replace('[', ' ').Replace(']', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !(w.Contains("://", StringComparison.Ordinal)
                          || w.StartsWith("www", StringComparison.OrdinalIgnoreCase)
                          || w.Contains('@')));
        var schoon = string.Join(' ', woorden);
        return schoon.Length > MaxWaarschuwingLengte ? schoon[..MaxWaarschuwingLengte].TrimEnd() + "…" : schoon;
    }

    /// <summary>De gezuiverde waarschuwingen samengevoegd tot één tekst; lege waarschuwingen vallen weg.</summary>
    public static string WaarschuwingenSamen(IEnumerable<string>? waarschuwingen)
        => string.Join(" ", (waarschuwingen ?? []).Select(Waarschuwing).Where(w => w.Length > 0));

    /// <summary>Veiligt waarschuwingen vóór directe API-serialisatie, zodat ook UI/API-uitvoer één regel blijft.</summary>
    public static void SaneerWaarschuwingen(IList<string> waarschuwingen)
    {
        var schoon = waarschuwingen.Select(Waarschuwing).Where(w => w.Length > 0).ToArray();
        waarschuwingen.Clear();
        foreach (var waarschuwing in schoon) waarschuwingen.Add(waarschuwing);
    }

    private static string? Weergavenaam(string? waarde, int maxLengte)
    {
        // Een naam is één regel: regeleinden of control-tekens betekenen dat het geen naam is.
        if (waarde is null || waarde.Any(char.IsControl))
            return null;

        var tekst = NormaliseerWitruimte(waarde);
        if (tekst.Length == 0 || tekst.Length > maxLengte)
            return null;

        return NaamPatroon().IsMatch(tekst) && !BevatLinkOfMarkup(tekst) ? tekst : null;
    }

    private static string? LogAlsVervallen(string? ruw, string? schoon, string veld, ILogger? log)
    {
        if (schoon is null && !string.IsNullOrWhiteSpace(ruw))
            log?.LogInformation("Modelveld {Veld} vervallen: voldoet niet aan de toegestane vorm", veld);
        return schoon;
    }

    private static bool BevatLinkOfMarkup(string tekst)
        => tekst.Contains("://", StringComparison.Ordinal)
           || tekst.Contains("www", StringComparison.OrdinalIgnoreCase)
           || tekst.IndexOfAny(['@', '<', '>', '{', '}', '[', ']', '`']) >= 0
           || DomeinPatroon().IsMatch(tekst);

    private static string NormaliseerWitruimte(string? waarde)
    {
        if (string.IsNullOrWhiteSpace(waarde)) return "";
        var sb = new System.Text.StringBuilder(waarde.Length);
        foreach (var c in waarde) sb.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
        return Regex.Replace(sb.ToString(), @" {2,}", " ").Trim();
    }
}
