using Microsoft.Extensions.Logging;
using Planner.Shared.Infrastructure;
using System.Text.Json;
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
    string? LogoUrl,
    IReadOnlyDictionary<string, string>? LightColors = null,
    IReadOnlyDictionary<string, string>? DarkColors = null);

/// <summary>Uitkomst van de validatie vóór <c>PUT /api/beheer/theme</c>.</summary>
public enum ThemeValidatieStatus
{
    /// <summary>Alles geldig — de tier mag opslaan.</summary>
    Ok,

    /// <summary>Een kleurwaarde is geen geldige <c>#rrggbb</c> (HTTP 400).</summary>
    OngeldigeKleur,

    /// <summary>De club-website-URL is onbruikbaar of wijst naar een niet-toegestane bestemming (HTTP 400).</summary>
    OngeldigeWebsiteUrl,

    /// <summary>Een sleutel of waarde in het licht-/donkerpalet is niet toegestaan (HTTP 400).</summary>
    OngeldigPalet
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

    /// <summary>
    /// Het volledige kleurenpalet voor de lichte modus (#1254), als sleutel → hexwaarde. Bewust
    /// een dictionary en geen veld per kleur: het aantal kleuren groeit nog met epic #1249, en een
    /// veld per kleur betekent bij elke uitbreiding opnieuw een wijziging in beide tiers.
    /// <c>null</c> of leeg = niet ingesteld, de club valt terug op de vier platte velden hierboven.
    /// </summary>
    public Dictionary<string, string>? LightColors { get; set; }

    /// <summary>Als <see cref="LightColors"/>, voor de donkere modus.</summary>
    public Dictionary<string, string>? DarkColors { get; set; }
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
        ClubWebsiteUrl: "", FaviconUrl: null, LogoUrl: null, LightColors: null, DarkColors: null);

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
        logoUrl        = waarden.LogoUrl,
        lightColors    = waarden.LightColors,
        darkColors     = waarden.DarkColors
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

    /// <summary>Maximaal aantal kleuren in één modus-palet (#1254).</summary>
    public const int MaxPaletSleutels = 40;

    // Een paletsleutel wordt in de browser samengevoegd tot een CSS custom property
    // (`--theme-<sleutel>-light`). De sleutel komt uit een admin-request, dus hij wordt hier
    // vastgelegd op een vorm die geen CSS-syntaxis kan bevatten: begint met een kleine letter,
    // daarna alleen letters, cijfers en koppeltekens. Een admin is binnen dit deploymentmodel
    // vertrouwd (#393), dus dit is geen autorisatiegrens — maar een waarde die ongefilterd in een
    // stylesheet-property belandt hoort wél een vaste vorm te hebben.
    private static readonly Regex _paletSleutelRegex = new(@"^[a-z][a-zA-Z0-9-]{0,39}$", RegexOptions.Compiled);

    // #rrggbb of #rrggbbaa. De alpha-variant is nodig voor kleuren als de hover-schaduw, die in
    // app.css nu een rgba()-literal is; een vrije rgba()-string toestaan zou een willekeurige
    // tekenreeks in een CSS-property laten belanden.
    private static readonly Regex _paletWaardeRegex = new(@"^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$", RegexOptions.Compiled);

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
    /// Een paletwaarde is <c>#rrggbb</c> of <c>#rrggbbaa</c>. Strenger dan alleen "is dit een
    /// kleur": de waarde belandt in een CSS custom property, dus een vrije tekenreeks is geen optie.
    /// </summary>
    public static bool IsValidPaletWaarde(string? value) =>
        !string.IsNullOrWhiteSpace(value) && _paletWaardeRegex.IsMatch(value);

    /// <summary>Zie <see cref="_paletSleutelRegex"/> voor waarom de vorm van een sleutel vastligt.</summary>
    public static bool IsValidPaletSleutel(string? key) =>
        !string.IsNullOrWhiteSpace(key) && _paletSleutelRegex.IsMatch(key);

    /// <summary>
    /// Valideert één modus-palet: het aantal sleutels, en elke sleutel en waarde afzonderlijk.
    /// Itereert bewust over de dictionary in plaats van per kleur een eigen regel te hebben — het
    /// aantal kleuren groeit nog, en een regel per kleur is precies de duplicatie die #1248 ophief.
    /// <c>null</c> of leeg is geldig: de club valt dan terug op de vier platte kleuren.
    /// </summary>
    public static string? ValideerPalet(IReadOnlyDictionary<string, string>? palet, string naam)
    {
        if (palet == null || palet.Count == 0) return null;

        if (palet.Count > MaxPaletSleutels)
            return $"Te veel kleuren in {naam} (maximaal {MaxPaletSleutels}).";

        foreach (var (sleutel, waarde) in palet)
        {
            if (!IsValidPaletSleutel(sleutel))
                return $"Ongeldige kleurnaam in {naam}.";
            if (!IsValidPaletWaarde(waarde))
                return $"Ongeldige kleurwaarde voor '{sleutel}' in {naam}.";
        }

        return null;
    }

    /// <summary>
    /// Serialiseert een modus-palet naar de JSON die in de databasekolom gaat. <c>null</c> voor een
    /// leeg of ontbrekend palet, zodat de kolom <c>NULL</c> blijft en de terugval op de platte
    /// kleuren blijft werken.
    /// </summary>
    public static string? PaletNaarJson(IReadOnlyDictionary<string, string>? palet) =>
        palet == null || palet.Count == 0 ? null : JsonSerializer.Serialize(palet);

    /// <summary>
    /// Leest een modus-palet uit de databasekolom. Onleesbare of ongeldige inhoud geeft
    /// <c>null</c> in plaats van een uitzondering: een kapot palet mag nooit het hele
    /// thema-endpoint laten vallen — de club valt dan terug op de platte kleuren.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? PaletUitJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var palet = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (palet == null || palet.Count == 0) return null;
            return ValideerPalet(palet, "palet") == null ? palet : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Valideert de vier kleuren, beide modus-paletten, en — als er een club-website-URL is
    /// meegegeven — of die naar een toegestane publieke bestemming resolvet.
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

        // Paletten vóór de URL-controle: die laatste doet een DNS-lookup, en een afgewezen palet
        // hoeft dat niet te kosten.
        var paletFout = ValideerPalet(dto.LightColors, "het lichte kleurenpalet")
                     ?? ValideerPalet(dto.DarkColors, "het donkere kleurenpalet");
        if (paletFout != null)
            return new ThemeValidatieResultaat(ThemeValidatieStatus.OngeldigPalet, paletFout);

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
        if (!Uri.TryCreate(websiteUrl, UriKind.Absolute, out var uri)) return null;

        // Zelfde Unix-valkuil als in ResolveUrl (#1252): een opgeslagen waarde als "/pad" parseert
        // hier succesvol als file:-URI en levert dan een lege host op. Expliciet op schema en een
        // niet-lege host controleren, zodat er nooit een onzinnige allowlist-sleutel uit komt.
        if (!IsHttpOfHttps(uri) || string.IsNullOrEmpty(uri.Host)) return null;
        return uri.Host;
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
    /// (<c>data:</c>, <c>javascript:</c>, <c>file:</c>, ...) geeft <c>null</c>.
    /// <para>
    /// Er wordt bewust met <see cref="UriKind.RelativeOrAbsolute"/> geparsed en daarna op
    /// <see cref="Uri.IsAbsoluteUri"/> beslist, niet met <see cref="UriKind.Absolute"/> vooraf
    /// (#1252): op Unix — en dus op het Linux Consumption Plan waar dit draait — parseert
    /// <c>Uri.TryCreate("/favicon.ico", UriKind.Absolute, out _)</c> succesvol, als
    /// <c>file:</c>-URI. De oude volgorde nam daardoor altijd de absolute tak, vond schema
    /// <c>file</c> en gaf <c>null</c> terug; de relatieve tak was voor root-relatieve paden
    /// onbereikbaar. Gevolg: favicon en logo kwamen er in productie nooit uit, zonder foutmelding.
    /// Op Windows gaf dezelfde aanroep <c>false</c> en werkte het wél — een platformafhankelijke
    /// stilte.
    /// </para>
    /// </summary>
    public static string? ResolveUrl(string url, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var kandidaat)) return null;

        if (kandidaat.IsAbsoluteUri)
            return IsHttpOfHttps(kandidaat) ? kandidaat.ToString() : null;

        return Uri.TryCreate(baseUri, kandidaat, out var opgelost) && IsHttpOfHttps(opgelost)
            ? opgelost.ToString()
            : null;
    }

    private static bool IsHttpOfHttps(Uri uri) => uri.Scheme == "http" || uri.Scheme == "https";
}
