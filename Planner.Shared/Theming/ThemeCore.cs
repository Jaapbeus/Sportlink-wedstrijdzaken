using Microsoft.Extensions.Logging;
using Planner.Shared.Infrastructure;
using System.Text.RegularExpressions;

namespace Planner.Shared.Theming;

// Provider-onafhankelijke kern van de club-thema-API (#325, #422, #887, #1007), gedeeld tussen
// beide database-tiers (#1248). FunctionApp/Admin/AdminThemeFunction.cs en
// FunctionApp.Postgres/Admin/AdminThemeFunction.cs bevatten dezelfde regex-set, dezelfde
// _skipColors-lijst, dezelfde hexvalidatie, dezelfde SSRF-allowlist-flow en dezelfde
// default-kleuren als magic strings; het enige echte verschil was de databaseclient
// (SqlConnection vs. NpgsqlConnection) en de kolomnaam-casing. Die duplicatie is geen bewuste
// keuze voor thema-logica — de Postgres-tier is een generieke 1-op-1 poort (#887) — maar ze
// betekende wél dat elke wijziging aan het kleurmodel tweemaal met de hand moest, in twee
// bestanden die niets van elkaar weten en niet gedeeld getest worden.
//
// Zelfde precedent en zelfde vorm als FeedbackCore (#1130): een pure klasse zonder ASP.NET
// Core-afhankelijkheid, die statussen en resultaatrecords teruggeeft. De vertaling naar
// IActionResult en álle databasetoegang blijven bij de tier-specifieke entrypoint — precies de
// scheiding die docs/ARCHITECTUUR-DATABASE-TIERS.md §2 voorschrijft.

/// <summary>De kleuren en assets van een club-thema, los van hoe ze zijn opgeslagen.</summary>
public sealed record ThemeWaarden(
    string Primary,
    string Secondary,
    string Accent,
    string TextOnPrimary,
    string ClubWebsiteUrl,
    string? FaviconUrl,
    string? LogoUrl);

/// <summary>Uitkomst van de validatie vóór <c>PUT /api/beheer/theme</c>.</summary>
public enum ThemeValidatieStatus
{
    /// <summary>Alles geldig — de tier mag opslaan.</summary>
    Ok,

    /// <summary>Een kleurwaarde is geen geldige <c>#rrggbb</c> (HTTP 400).</summary>
    OngeldigeKleur,

    /// <summary>De club-website-URL is onbruikbaar of wijst naar een niet-toegestane bestemming (HTTP 400).</summary>
    OngeldigeWebsiteUrl
}

/// <summary>Resultaat van <see cref="ThemeCore.ValideerUpdateAsync"/>.</summary>
public sealed record ThemeValidatieResultaat(ThemeValidatieStatus Status, string? Foutmelding);

/// <summary>Uitkomst van <c>POST /api/beheer/theme/extract</c>.</summary>
public enum ThemeExtractieStatus
{
    /// <summary>Geëxtraheerd (nul kleuren is een geldige uitkomst, geen fout).</summary>
    Ok,

    /// <summary>URL ontbreekt, is niet absoluut, of heeft een ander schema dan http/https (HTTP 400).</summary>
    OngeldigeUrl,

    /// <summary>Host staat niet op de allowlist uit <c>AppSettings</c>, of die is leeg (HTTP 400).</summary>
    HostNietToegestaan,

    /// <summary>Bestemming door <see cref="SsrfProtection"/> geweigerd (HTTP 400).</summary>
    BestemmingGeblokkeerd,

    /// <summary>De website gaf geen bruikbaar antwoord (HTTP 502).</summary>
    OphalenMislukt
}

/// <summary>Resultaat van <see cref="ThemeCore.ExtraheerAsync"/>.</summary>
public sealed record ThemeExtractieResultaat(
    ThemeExtractieStatus Status,
    string? Foutmelding,
    IReadOnlyList<string>? Colors = null,
    string? FaviconUrl = null,
    string? LogoUrl = null);

/// <summary>
/// Requestbody van <c>PUT /api/beheer/theme</c>. Stond tot #1248 als <c>internal sealed class</c>
/// in beide <c>AdminThemeFunction.cs</c>-bestanden — twee losse klassen die hetzelfde
/// JSON-contract moesten blijven beschrijven zonder dat iets dat afdwong.
/// </summary>
public sealed class ThemeUpdateRequest
{
    public string? Primary        { get; set; }
    public string? Secondary      { get; set; }
    public string? Accent         { get; set; }
    public string? TextOnPrimary  { get; set; }
    public string? ClubWebsiteUrl { get; set; }
    public string? FaviconUrl     { get; set; }
    public string? LogoUrl        { get; set; }
}

/// <summary>
/// Tier-onafhankelijke thema-logica: kleur-/favicon-/logo-extractie uit de HTML van de
/// clubwebsite, hexvalidatie, de SSRF-allowlist-orkestratie en de standaardkleuren.
/// </summary>
public static class ThemeCore
{
    // De standaardkleuren van de applicatie. Bewust géén clubkleuren: dit is wat een club ziet
    // vóórdat er een eigen thema is ingesteld (Bootstrap-blauw), niet de kleur van een
    // specifieke vereniging — zie de regel "geen club-specifieke strings in code" in CLAUDE.md.
    public const string DefaultPrimaryColor      = "#1b6ec2";
    public const string DefaultSecondaryColor    = "#6c757d";
    public const string DefaultAccentColor       = "#0071c1";
    public const string DefaultTextOnPrimaryColor = "#ffffff";

    /// <summary>Het thema zoals een club het krijgt zolang er niets is ingesteld.</summary>
    public static ThemeWaarden Standaard { get; } = new(
        DefaultPrimaryColor, DefaultSecondaryColor, DefaultAccentColor, DefaultTextOnPrimaryColor,
        ClubWebsiteUrl: "", FaviconUrl: null, LogoUrl: null);

    /// <summary>
    /// De responsvorm van <c>GET /api/beheer/theme</c>. Eén plek, zodat beide tiers gegarandeerd
    /// hetzelfde JSON-contract teruggeven; de veldnamen staan hier expliciet in de vorm waarin ze
    /// over de lijn gaan, dus onafhankelijk van de naamgevingspolicy van de serializer.
    /// </summary>
    public static object BouwResponse(ThemeWaarden waarden) => new
    {
        primary        = waarden.Primary,
        secondary      = waarden.Secondary,
        accent         = waarden.Accent,
        textOnPrimary  = waarden.TextOnPrimary,
        clubWebsiteUrl = waarden.ClubWebsiteUrl,
        faviconUrl     = waarden.FaviconUrl,
        logoUrl        = waarden.LogoUrl
    };

    private static readonly Regex _hexColorRegex      = new(@"#([0-9a-fA-F]{6})\b", RegexOptions.Compiled);
    private static readonly Regex _hexColorValidRegex = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private static readonly Regex _faviconRegex       = new(@"<link[^>]*rel=[""'](?:shortcut icon|icon)[""'][^>]*href=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _faviconAltRegex    = new(@"<link[^>]*href=[""']([^""']+)[""'][^>]*rel=[""'](?:shortcut icon|icon)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _ogImageRegex       = new(@"<meta[^>]*property=[""']og:image[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _ogImageAltRegex    = new(@"<meta[^>]*content=[""']([^""']+)[""'][^>]*property=[""']og:image[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _appleTouchRegex    = new(@"<link[^>]*rel=[""']apple-touch-icon[""'][^>]*href=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Te algemeen om een merkkleur te zijn: die komen op vrijwel elke site voor en zouden de
    // frequentielijst domineren.
    private static readonly HashSet<string> _skipColors = new(StringComparer.OrdinalIgnoreCase)
        { "#ffffff", "#000000", "#eeeeee", "#cccccc", "#f0f0f0", "#333333" };

    /// <summary>Maximaal aantal kleuren dat de extractie teruggeeft, aflopend op voorkomen.</summary>
    public const int MaxGeextraheerdeKleuren = 8;

    /// <summary>
    /// De gedeelde, SSRF-beschermde <see cref="HttpClient"/> voor thema-extractie (#1007):
    /// redirects staan uit en worden begrensd/opnieuw gevalideerd gevolgd; elke daadwerkelijke
    /// connectie (initieel én elke hop) resolvet zelf en weigert privé/loopback/link-local
    /// bestemmingen.
    /// </summary>
    public static HttpClient HttpClient { get; } =
        SsrfProtection.CreateHttpClient(TimeSpan.FromSeconds(10), "SportlinkAdmin/2.0");

    /// <summary>
    /// Een lege of ontbrekende waarde is geldig: het veld is optioneel en valt dan terug op de
    /// standaardkleur.
    /// </summary>
    public static bool IsValidHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return _hexColorValidRegex.IsMatch(value);
    }

    /// <summary>
    /// Valideert de vier kleuren en — als er een club-website-URL is meegegeven — of die naar een
    /// toegestane publieke bestemming resolvet.
    /// <para>
    /// Die laatste controle staat bewust al bij het opslaan en niet pas bij extractie (#1007):
    /// anders kan dezelfde admin die de allowlist beheert hem op een intern adres zetten.
    /// </para>
    /// </summary>
    public static async Task<ThemeValidatieResultaat> ValideerUpdateAsync(
        ThemeUpdateRequest dto,
        Func<string, Task<System.Net.IPAddress?>>? resolverOverride = null)
    {
        if (!IsValidHexColor(dto.Primary))
            return new ThemeValidatieResultaat(ThemeValidatieStatus.OngeldigeKleur, "Ongeldige primary kleur.");
        if (!IsValidHexColor(dto.Secondary))
            return new ThemeValidatieResultaat(ThemeValidatieStatus.OngeldigeKleur, "Ongeldige secondary kleur.");
        if (!IsValidHexColor(dto.Accent))
            return new ThemeValidatieResultaat(ThemeValidatieStatus.OngeldigeKleur, "Ongeldige accent kleur.");
        if (!IsValidHexColor(dto.TextOnPrimary))
            return new ThemeValidatieResultaat(ThemeValidatieStatus.OngeldigeKleur, "Ongeldige textOnPrimary kleur.");

        if (string.IsNullOrWhiteSpace(dto.ClubWebsiteUrl))
            return new ThemeValidatieResultaat(ThemeValidatieStatus.Ok, null);

        if (!Uri.TryCreate(dto.ClubWebsiteUrl, UriKind.Absolute, out var websiteUri))
            return new ThemeValidatieResultaat(ThemeValidatieStatus.OngeldigeWebsiteUrl, "Ongeldige club-website-URL.");
        if (!SsrfProtection.TryValidateUriShape(websiteUri, out var shapeError))
            return new ThemeValidatieResultaat(ThemeValidatieStatus.OngeldigeWebsiteUrl, shapeError);

        var resolver = resolverOverride ?? (host => SsrfProtection.ResolveAllowedAddressAsync(host));
        var resolvedAddress = await resolver(websiteUri.Host);
        if (resolvedAddress == null)
            return new ThemeValidatieResultaat(ThemeValidatieStatus.OngeldigeWebsiteUrl,
                "Club-website-URL resolveert niet naar een toegestaan publiek adres.");

        return new ThemeValidatieResultaat(ThemeValidatieStatus.Ok, null);
    }

    /// <summary>
    /// Vertaalt de opgeslagen <c>ThemeClubWebsiteUrl</c> naar de hostnaam die de allowlist voor
    /// extractie vormt (#422). Een lege of onparsebare waarde geeft <c>null</c> — de tier
    /// blokkeert de extractie dan.
    /// </summary>
    public static string? HostUitWebsiteUrl(string? websiteUrl)
    {
        if (string.IsNullOrWhiteSpace(websiteUrl)) return null;
        return Uri.TryCreate(websiteUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>
    /// Haalt de clubwebsite op en extraheert kleuren, favicon en logo.
    /// <para>
    /// De aanroeper levert de allowlist-host aan uit de eigen database — dat is het enige
    /// tier-specifieke deel. De vergelijking zelf staat hier, zodat beide tiers gegarandeerd
    /// dezelfde controle uitvoeren; <c>null</c> betekent: geen clubwebsite ingesteld of database
    /// niet beschikbaar, dus blokkeren.
    /// </para>
    /// <para>
    /// <paramref name="toegestaneHostProvider"/> is bewust lui: de vorm van de URL wordt eerst
    /// gecontroleerd, zodat een onbruikbare URL geen databaseaanroep kost. Dat is ook de volgorde
    /// die beide tiers vóór #1248 al hadden.
    /// </para>
    /// </summary>
    public static async Task<ThemeExtractieResultaat> ExtraheerAsync(
        string? url,
        Func<Task<string?>> toegestaneHostProvider,
        ILogger log,
        HttpClient? httpClientOverride = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ThemeExtractieResultaat(ThemeExtractieStatus.OngeldigeUrl, "Parameter 'url' ontbreekt.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
            return new ThemeExtractieResultaat(ThemeExtractieStatus.OngeldigeUrl, "Ongeldige URL. Alleen http/https toegestaan.");

        // SSRF-bescherming via allowlist: alleen ThemeClubWebsiteUrl uit AppSettings is toegestaan.
        // Elimineert TOCTOU/DNS-rebinding volledig — geen DNS-lookup nodig. (#422)
        var toegestaneHost = await toegestaneHostProvider();
        if (toegestaneHost == null || !parsedUri.Host.Equals(toegestaneHost, StringComparison.OrdinalIgnoreCase))
            return new ThemeExtractieResultaat(ThemeExtractieStatus.HostNietToegestaan,
                "URL-domein is niet toegestaan. Stel eerst de club-website in via het thema-scherm.");

        try
        {
            // De daadwerkelijke IP-validatie zit in de ConnectCallback van de client en geldt dus
            // voor elke hop, niet alleen deze initiële URL (#1007).
            using var response = await SsrfProtection.GetWithBoundedRedirectsAsync(
                httpClientOverride ?? HttpClient, parsedUri);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();

            var colors = ExtractColors(html);
            var faviconUrl = ExtractFaviconUrl(html, parsedUri);
            var logoUrl = ExtractLogoUrl(html, parsedUri);
            log.LogInformation("Assets geëxtraheerd uit {Host}: {Count} kleuren, favicon={Fav}, logo={Logo}",
                parsedUri.Host, colors.Count, faviconUrl != null, logoUrl != null);
            return new ThemeExtractieResultaat(ThemeExtractieStatus.Ok, null, colors, faviconUrl, logoUrl);
        }
        catch (SsrfBlockedException ex)
        {
            log.LogWarning(ex, "Extractie geweigerd door SSRF-bescherming: {Host}", parsedUri.Host);
            return new ThemeExtractieResultaat(ThemeExtractieStatus.BestemmingGeblokkeerd, "URL-bestemming is niet toegestaan.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Ophalen website mislukt: {Host}", parsedUri.Host);
            return new ThemeExtractieResultaat(ThemeExtractieStatus.OphalenMislukt, "Website kon niet worden opgehaald.");
        }
    }

    /// <summary>
    /// De meest voorkomende hexkleuren in de ruwe HTML, aflopend op frequentie. Leest bewust
    /// alleen letterlijke <c>#rrggbb</c>-voorkomens: geen CSS-parsing, geen rendering — voor
    /// CMS-sites die hun merkkleuren in een extern stylesheet zetten kan dat weinig of niets
    /// opleveren, en dat is een geldige uitkomst, geen fout.
    /// </summary>
    public static List<string> ExtractColors(string html)
    {
        var matches = _hexColorRegex.Matches(html);
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            var color = m.Value.ToLowerInvariant();
            if (_skipColors.Contains(color)) continue;
            freq[color] = freq.TryGetValue(color, out var c) ? c + 1 : 1;
        }
        return freq.OrderByDescending(kv => kv.Value).Take(MaxGeextraheerdeKleuren).Select(kv => kv.Key).ToList();
    }

    /// <summary>Valt terug op <c>/favicon.ico</c> als er geen <c>&lt;link rel="icon"&gt;</c> staat.</summary>
    public static string? ExtractFaviconUrl(string html, Uri baseUri)
    {
        var m = _faviconRegex.Match(html);
        if (!m.Success) m = _faviconAltRegex.Match(html);
        var href = m.Success ? m.Groups[1].Value : "/favicon.ico";
        return ResolveUrl(href, baseUri);
    }

    /// <summary>Zoekt achtereenvolgens <c>og:image</c> en <c>apple-touch-icon</c>.</summary>
    public static string? ExtractLogoUrl(string html, Uri baseUri)
    {
        var m = _ogImageRegex.Match(html);
        if (!m.Success) m = _ogImageAltRegex.Match(html);
        if (!m.Success) m = _appleTouchRegex.Match(html);
        if (!m.Success) return null;
        return ResolveUrl(m.Groups[1].Value, baseUri);
    }

    /// <summary>
    /// Maakt een absolute http/https-URL van een mogelijk relatieve verwijzing. Een ander schema
    /// (<c>data:</c>, <c>javascript:</c>, ...) geeft <c>null</c>.
    /// </summary>
    public static string? ResolveUrl(string url, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
            return abs.Scheme == "http" || abs.Scheme == "https" ? abs.ToString() : null;
        if (Uri.TryCreate(baseUri, url, out var rel))
            return rel.Scheme == "http" || rel.Scheme == "https" ? rel.ToString() : null;
        return null;
    }
}
