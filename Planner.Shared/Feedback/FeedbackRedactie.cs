using System.Text.RegularExpressions;

namespace Planner.Shared.Feedback;

/// <summary>
/// Redactie van persoonsgegevens en geheimen uit de technische context van een feedbackmelding
/// (#764, DPO-voorwaarde: console-fouten, mislukte API-aanroepen en navigatiespoor mogen geen
/// persoonsgegevens bevatten).
///
/// <para>
/// <b>Eén bron, twee plekken.</b> Dit bestand hangt uitsluitend van de BCL af en wordt als gelinkt
/// bronbestand ook in BlazorAdmin gecompileerd (zie <c>scripts/ci/check-gelinkte-bronbestanden.sh</c>,
/// #1461): de browser redigeert vóór verzending, wat het contextpaneel laat zien is dus al
/// geredigeerd, en de server past dezelfde regels nogmaals toe omdat een client nooit vertrouwd
/// wordt. Een tweede set regels zou precies de drift veroorzaken die #1248 en #1130 oplosten.
/// </para>
/// <para>
/// <b>Wat dit niet is.</b> Regex-redactie herkent e-mailadressen, telefoonnummers, GUID's, tokens,
/// querystrings, lange cijferreeksen en waarden achter sleutels als <c>naam</c> of <c>wachtwoord</c>.
/// Een naam in vrije tekst herkent het alleen als die naam bekend is (de weergavenaam van de melder,
/// zie <see cref="RedigeerNaam"/>). Dit is dataminimalisatie, geen garantie — de technische context
/// gaat daarom nooit het publieke GitHub-issue in, en wordt na 90 dagen gewist.
/// </para>
/// Geen <c>RegexOptions.Compiled</c>: dat faalt in Blazor WebAssembly.
/// </summary>
public static class FeedbackRedactie
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static Regex R(string patroon, RegexOptions opties = RegexOptions.None) =>
        new(patroon, opties | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex Email = R(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}");
    private static readonly Regex Jwt = R(@"eyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.?[A-Za-z0-9_\-]*");
    private static readonly Regex Bearer = R(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase);
    private static readonly Regex Guid = R(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b");
    private static readonly Regex Telefoon = R(@"(\+31|0031|06)[\s\-]?\d{2}[\s\-]?\d{6,8}|0\d{1,2}[\s\-]\d{6,8}");

    // Een query-string of fragment achter een pad of URL ("/teams?zoek=JO13-1", "https://x/y?id=3#a").
    // Alles vanaf het vraagteken verdwijnt: daar staan zoekwaarden, ID's en soms namen.
    private static readonly Regex Querystring = R(@"(?<pad>(?:https?://)?[A-Za-z0-9\-._~%/:]*/[A-Za-z0-9\-._~%/{}]*)[?#][^\s""'<>)\]]*");

    // Sleutel=waarde en "sleutel":"waarde" voor sleutels die persoonsgegevens of geheimen dragen.
    private static readonly Regex GevoeligeSleutel = R(
        @"(?<sleutel>\b(?:wachtwoord|password|pwd|secret|token|apikey|api_key|code|sig|signature|email|e-mail|emailadres|upn|naam|name|displayname|voornaam|achternaam|fullname|username|gebruikersnaam)\b[""']?\s*[:=]\s*[""']?)(?<waarde>[^\s""',;&}\]]+)",
        RegexOptions.IgnoreCase);

    // Numerieke ID's als padsegment: /teams/12345 → /teams/{id}.
    private static readonly Regex PadId = R(@"(?<=/)\d{3,}(?=/|$|\s|[?#""'])");

    // Lange cijferreeksen (lidnummers, BSN, IBAN-delen, telefoon zonder scheiding).
    private static readonly Regex LangeCijfers = R(@"\b\d{7,}\b");

    // Lange aaneengesloten token-achtige reeksen (hex/base64/sleutel).
    private static readonly Regex LangeToken = R(@"\b[A-Za-z0-9_\-]{32,}\b");

    /// <summary>Past alle redactieregels toe op één stuk tekst en kapt het af op <paramref name="maxLengte"/>.</summary>
    public static string Redigeer(string? tekst, int maxLengte = 300)
    {
        if (string.IsNullOrWhiteSpace(tekst)) return "";
        try
        {
            var t = tekst.Trim();
            t = Jwt.Replace(t, "[token]");
            t = Bearer.Replace(t, "Bearer [token]");
            t = Email.Replace(t, "[e-mail]");
            t = Querystring.Replace(t, "${pad}");
            t = GevoeligeSleutel.Replace(t, "${sleutel}[verborgen]");
            t = Guid.Replace(t, "[id]");
            t = Telefoon.Replace(t, "[telefoon]");
            t = PadId.Replace(t, "{id}");
            t = LangeCijfers.Replace(t, "[nummer]");
            t = LangeToken.Replace(t, "[token]");
            return t.Length > maxLengte ? t[..maxLengte] + "…" : t;
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathologische invoer: liever niets meesturen dan ongeredigeerd.
            return "[niet te redigeren]";
        }
    }

    /// <summary>
    /// Vervangt elk voorkomen van de naam (en van elk naamdeel van minimaal drie tekens) door
    /// <c>[naam]</c>. Bedoeld voor de weergavenaam van de melder, die de server uit het principal
    /// kent: zo blijft die naam ook in een vrije-tekstfout niet achter.
    /// </summary>
    public static string RedigeerNaam(string? tekst, string? naam)
    {
        if (string.IsNullOrEmpty(tekst) || string.IsNullOrWhiteSpace(naam)) return tekst ?? "";
        var resultaat = tekst;
        var delen = naam.Split([' ', ',', '.', '-', '_', '@'], StringSplitOptions.RemoveEmptyEntries)
            .Where(d => d.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var deel in delen)
        {
            try
            {
                resultaat = Regex.Replace(resultaat, @"\b" + Regex.Escape(deel) + @"\b", "[naam]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
            }
            catch (RegexMatchTimeoutException) { /* overslaan: de overige regels hebben al gedraaid */ }
        }
        return resultaat;
    }

    /// <summary>
    /// Een route (<c>/teams?zoek=JO13-1</c>) wordt teruggebracht tot het pad zonder querystring,
    /// fragment of numerieke ID-segmenten.
    /// </summary>
    public static string RedigeerRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "";
        var pad = route.Trim();
        var einde = pad.IndexOfAny(['?', '#']);
        if (einde >= 0) pad = pad[..einde];
        return Redigeer(pad, 200);
    }
}
