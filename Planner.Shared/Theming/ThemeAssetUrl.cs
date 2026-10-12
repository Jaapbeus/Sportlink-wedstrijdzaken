namespace Planner.Shared.Theming;

/// <summary>
/// Regels voor een opgeslagen favicon-/logo-URL van een club-thema (#1675): de validatie bij het
/// opslaan, het omzetten van een http-URL naar https en de controle die de extractie gebruikt om
/// alleen opslaanbare URL's voor te stellen. Eén plek, zodat opslaan en extractie dezelfde regels
/// volgen.
/// </summary>
internal static class ThemeAssetUrl
{
    /// <summary>Maximale lengte van een opgeslagen favicon-/logo-URL.</summary>
    internal const int MaxAssetUrlLengte = 300;

    /// <summary>
    /// Valideert een optionele favicon-/logo-URL: leeg mag, anders absoluut https, niet langer dan
    /// <see cref="MaxAssetUrlLengte"/>, een niet-lege host en geen controletekens. Geeft de
    /// foutmelding of <c>null</c>. Geparsed met <see cref="UriKind.RelativeOrAbsolute"/> (#1252).
    /// De melding zegt wat er mag en hoe de beheerder verder kan, omdat een opgeslagen waarde die
    /// niet aan de eisen voldoet elke volgende opslag tegenhoudt tot het veld is aangepast.
    /// </summary>
    internal static string? ValideerAssetUrl(string? url, string naam)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var fout = $"De {naam} is ongeldig of te lang (maximaal {MaxAssetUrlLengte} tekens, https). "
                 + "Leeg het veld of vul een geldige https-URL in.";
        if (url.Length > MaxAssetUrlLengte || url.Any(char.IsControl)) return fout;
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri) || !uri.IsAbsoluteUri) return fout;
        return uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host) ? null : fout;
    }

    /// <summary>
    /// Zet een absolute http-URL om naar https met dezelfde host, hetzelfde pad en dezelfde query;
    /// een expliciete standaardpoort 80 vervalt. Elke andere waarde (leeg, relatief, al https, een
    /// ander schema of onparsebaar) komt ongewijzigd terug, zodat <see cref="ValideerAssetUrl"/>
    /// daarover blijft beslissen.
    /// </summary>
    internal static string? NaarHttps(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl))
            return url;
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri) || !uri.IsAbsoluteUri)
            return url;
        if (uri.Scheme != Uri.UriSchemeHttp || string.IsNullOrEmpty(uri.Host))
            return url;

        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps };
        if (uri.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// De vorm waarin een asset-URL wordt opgeslagen: witruimte eromheen vervalt en een absolute
    /// http-URL wordt https (<see cref="NaarHttps"/>). <c>null</c> blijft <c>null</c>.
    /// </summary>
    internal static string? Normaliseer(string? url) => url == null ? null : NaarHttps(url.Trim());

    /// <summary>
    /// De vorm waarin een voorgestelde asset-URL opgeslagen zou worden (zie <see cref="Normaliseer"/>), of
    /// <c>null</c> als <see cref="ValideerAssetUrl"/> die vorm bij opslaan zou weigeren.
    /// </summary>
    internal static string? AlleenOpslaanbaar(string? url)
    {
        var kandidaat = Normaliseer(url);
        if (kandidaat == null)
            return null;
        return ValideerAssetUrl(kandidaat, "asset-URL") == null ? kandidaat : null;
    }
}
