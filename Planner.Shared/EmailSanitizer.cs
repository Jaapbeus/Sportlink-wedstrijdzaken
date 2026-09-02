using System.Net;
using System.Text.RegularExpressions;

namespace Planner.Shared;

/// <summary>
/// Utility voor het saneren van e-mailinhoud:
/// - foutmeldingen anonimiseren vóór DB-opslag of logging (#420),
/// - HTML strippen uit inkomende berichttekst,
/// - gebruikerstekst veilig maken vóórdat die als HTML-mail wordt verstuurd.
/// <para>
/// Verhuisd naar <c>Planner.Shared</c> bij issue 888 vervolg (§43): puur (geen SQL, geen
/// instellingencache) en veiligheidsrelevant — sanitisatie hoort per definitie één implementatie
/// en één testsuite te hebben, precies wat de doc-comment van EmailGraphService al voorschreef.
/// Daarom <c>public</c> i.p.v. <c>internal</c>: het is nu een eigen assembly.
/// </para>
/// </summary>
public static partial class EmailSanitizer
{
    // Verwijdert e-mailadressen en knipt af op 200 tekens. (#420)
    public static string SanitizeFoutMelding(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Onbekende fout";
        var gesaneerd = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
            "[e-mail]");
        return gesaneerd.Length > 200 ? gesaneerd[..200] + "…" : gesaneerd;
    }

    /// <summary>
    /// Maximaal aantal decode+strip-rondes in <see cref="StripHtml"/>. Elke ronde pelt één laag
    /// HTML-encodering af. De lus stopt zodra de tekst niet meer verandert; deze bovengrens is er
    /// alleen om een oneindige lus bij pathologische invoer uit te sluiten.
    /// </summary>
    private const int MaxSanitisatieRondes = 5;

    /// <summary>
    /// Verwijdert HTML uit inkomende berichttekst en normaliseert whitespace.
    ///
    /// Decoderen en strippen gebeuren afwisselend tot de tekst stabiel is. Dat is nodig omdat
    /// dubbel-geëncodeerde markup (letterlijk "&amp;amp;lt;a href=…&amp;amp;gt;" in de body) in de
    /// eerste ronde geen tag is voor de regex, maar door de entiteit-decodering daarna alsnog
    /// echte markup wordt. Met alleen "strip, dan decode" overleefde zulke markup de sanitisatie
    /// en kwam hij als klikbare (phishing-)link in de doorgestuurde mail terecht.
    /// </summary>
    public static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var tekst = html;
        for (var ronde = 0; ronde < MaxSanitisatieRondes; ronde++)
        {
            // Decoderen vóór strippen — anders ontsnapt geëncodeerde markup aan de tag-regex.
            // WebUtility.HtmlDecode dekt ook numerieke entiteiten (&#60;, &#x3c;), die de eerdere
            // handmatige Replace-reeks niet zag.
            var gedecodeerd = WebUtility.HtmlDecode(tekst);

            // Script- en style-blokken inclusief inhoud weg: anders belandt de JS/CSS-broncode als
            // leestekst in het bericht (en daarmee ook in de AI-prompt).
            var zonderBlokken = ScriptStyleBlokRegex().Replace(gedecodeerd, " ");

            var gestript = HtmlTagRegex().Replace(zonderBlokken, " ");

            if (gestript == tekst)
                break; // stabiel: niets meer te decoderen of te strippen

            tekst = gestript;
        }

        // Vangnet: markup die pas uit de laatste decode-ronde ontstond mag nooit ongestript blijven.
        tekst = HtmlTagRegex().Replace(tekst, " ");

        return WhitespaceRegex().Replace(tekst, " ").Trim();
    }

    /// <summary>
    /// Tags die als HTML mogen doorlopen naar een uitgaande mail. De vergelijking is een exacte
    /// match op de volledige tag: onze eigen mail-templates gebruiken uitsluitend attribuutloze
    /// opmaaktags, dus alles met een attribuut (&lt;a href=…&gt;, &lt;img src=…&gt;,
    /// &lt;p onclick=…&gt;) valt automatisch buiten de lijst. Geen attribuut-parsing nodig —
    /// en daardoor geen ruimte voor subtiele parse-fouten.
    /// </summary>
    private static readonly HashSet<string> ToegestaneHtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "<p>", "</p>", "<br>", "<br/>", "<br />", "<hr>", "<hr/>", "<hr />",
        "<strong>", "</strong>", "<em>", "</em>", "<b>", "</b>", "<i>", "</i>",
        "<ul>", "</ul>", "<ol>", "</ol>", "<li>", "</li>"
    };

    /// <summary>
    /// Verwijdert elke tag die niet in <see cref="ToegestaneHtmlTags"/> staat, inclusief de
    /// inhoud van script- en style-blokken. Entiteiten worden NIET gedecodeerd: al ge-escapete
    /// gebruikerstekst moet als leestekst blijven staan.
    /// </summary>
    public static string SanitizeHtmlAllowlist(string html)
    {
        if (string.IsNullOrEmpty(html))
            return "";

        var zonderBlokken = ScriptStyleBlokRegex().Replace(html, "");
        return HtmlTagRegex().Replace(
            zonderBlokken,
            m => ToegestaneHtmlTags.Contains(m.Value) ? m.Value : "");
    }

    /// <summary>
    /// Maakt een body veilig om als HTML-mail te versturen.
    ///
    /// Er komen twee soorten body binnen:
    /// 1. HTML die wij zelf hebben opgebouwd (admin-doorsturen): gebruikerstekst is daar al
    ///    ge-escaped en de opmaak zit in attribuutloze tags uit onze eigen template.
    /// 2. Platte tekst met \n-regeleindes (doorgestuurde vraag uit de mailbox) waarin ruwe tekst
    ///    van een externe afzender is ingesloten.
    ///
    /// Beide takken zijn veilig: tak 1 laat alleen tags uit de allowlist staan, tak 2 escapet
    /// alles. De detectie bepaalt dus uitsluitend de opmaak, nooit de veiligheid — een afzender
    /// die zijn tekst als "eigen HTML" laat doorgaan, krijgt nog steeds geen link, afbeelding,
    /// script of style in de mail.
    /// </summary>
    public static string BouwVeiligeHtmlBody(string body)
    {
        if (string.IsNullOrEmpty(body))
            return "";

        if (EigenOpmaakTagRegex().IsMatch(body))
            return SanitizeHtmlAllowlist(body);

        return WebUtility.HtmlEncode(body)
            .Replace("\r\n", "\n")
            .Replace("\n", "<br />");
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleBlokRegex();

    // Blok-opmaak uit onze eigen templates — herkent "dit is al HTML" (zie BouwVeiligeHtmlBody).
    [GeneratedRegex(@"</?(?:p|br|hr|ul|ol|li)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex EigenOpmaakTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
