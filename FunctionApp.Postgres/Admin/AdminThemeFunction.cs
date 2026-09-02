using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminThemeFunction.cs</c> (#887). Vertaling:
/// <c>[dbo].[AppSettings]</c> → <c>public.appsettings</c> (incl. <c>faviconurl</c>/<c>logourl</c>,
/// zie <c>005_appsettings_theme_assets.sql</c>). De HTML-scraping/SSRF-allowlist-logica in
/// <c>Extract</c> is ongewijzigd gekopieerd — die is databasetier-onafhankelijk.
/// </summary>
public static class AdminThemeFunction
{
    private const string DefaultPrimaryColor = "#1b6ec2";
    private const string DefaultSecondaryColor = "#6c757d";
    private const string DefaultAccentColor = "#0071c1";
    private const string DefaultTextOnPrimaryColor = "#ffffff";

    private static readonly HttpClient _httpClient;
    private static readonly Regex _hexColorRegex    = new(@"#([0-9a-fA-F]{6})\b", RegexOptions.Compiled);
    private static readonly Regex _hexColorValidRegex = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private static readonly Regex _faviconRegex     = new(@"<link[^>]*rel=[""'](?:shortcut icon|icon)[""'][^>]*href=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _faviconAltRegex  = new(@"<link[^>]*href=[""']([^""']+)[""'][^>]*rel=[""'](?:shortcut icon|icon)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _ogImageRegex     = new(@"<meta[^>]*property=[""']og:image[""'][^>]*content=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _ogImageAltRegex  = new(@"<meta[^>]*content=[""']([^""']+)[""'][^>]*property=[""']og:image[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _appleTouchRegex  = new(@"<link[^>]*rel=[""']apple-touch-icon[""'][^>]*href=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static AdminThemeFunction()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(AdminEndpoint.OutboundHttpTimeoutSeconds) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", AdminEndpoint.OutboundUserAgent);
    }

    [Function("AdminThemeGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/theme")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminThemeGet");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                SELECT themecolorprimary, themecolorsecondary, themecoloraccent,
                       themecolortextonprimary, themeclubwebsiteurl,
                       faviconurl, logourl
                FROM public.appsettings
                WHERE clubcode = @clubcode", connection);
            command.Parameters.AddWithValue("clubcode", clubCode);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return new OkObjectResult(DefaultTheme());

            return new OkObjectResult(new
            {
                primary        = reader.IsDBNull(0) ? DefaultPrimaryColor : reader.GetString(0),
                secondary      = reader.IsDBNull(1) ? DefaultSecondaryColor : reader.GetString(1),
                accent         = reader.IsDBNull(2) ? DefaultAccentColor : reader.GetString(2),
                textOnPrimary  = reader.IsDBNull(3) ? DefaultTextOnPrimaryColor : reader.GetString(3),
                clubWebsiteUrl = reader.IsDBNull(4) ? ""        : reader.GetString(4),
                faviconUrl     = reader.IsDBNull(5) ? null      : reader.GetString(5),
                logoUrl        = reader.IsDBNull(6) ? null      : reader.GetString(6)
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen thema");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminThemePut")]
    public static async Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/theme")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminThemePut");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
        try
        {
            string body;
            using (var sr = new System.IO.StreamReader(req.Body))
                body = await sr.ReadToEndAsync();

            ThemeUpdateRequest? dto = null;
            try
            {
                dto = JsonSerializer.Deserialize<ThemeUpdateRequest>(body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch { }

            if (dto == null)
                return new BadRequestObjectResult(new { error = "Ongeldige JSON." });

            if (!IsValidHexColor(dto.Primary))
                return new BadRequestObjectResult(new { error = "Ongeldige primary kleur." });
            if (!IsValidHexColor(dto.Secondary))
                return new BadRequestObjectResult(new { error = "Ongeldige secondary kleur." });
            if (!IsValidHexColor(dto.Accent))
                return new BadRequestObjectResult(new { error = "Ongeldige accent kleur." });
            if (!IsValidHexColor(dto.TextOnPrimary))
                return new BadRequestObjectResult(new { error = "Ongeldige textOnPrimary kleur." });

            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                UPDATE public.appsettings
                SET themecolorprimary       = @primary,
                    themecolorsecondary     = @secondary,
                    themecoloraccent        = @accent,
                    themecolortextonprimary = @textonprimary,
                    themeclubwebsiteurl     = @websiteurl,
                    faviconurl              = @faviconurl,
                    logourl                 = @logourl
                WHERE clubcode             = @clubcode", connection);
            command.Parameters.AddWithValue("primary",        dto.Primary       ?? DefaultPrimaryColor);
            command.Parameters.AddWithValue("secondary",      dto.Secondary     ?? DefaultSecondaryColor);
            command.Parameters.AddWithValue("accent",         dto.Accent        ?? DefaultAccentColor);
            command.Parameters.AddWithValue("textonprimary",  dto.TextOnPrimary ?? DefaultTextOnPrimaryColor);
            command.Parameters.AddWithValue("websiteurl",     (object?)dto.ClubWebsiteUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("faviconurl",     (object?)dto.FaviconUrl     ?? DBNull.Value);
            command.Parameters.AddWithValue("logourl",        (object?)dto.LogoUrl        ?? DBNull.Value);
            command.Parameters.AddWithValue("clubcode",       clubCode);
            await command.ExecuteNonQueryAsync();

            log.LogInformation("Club-thema bijgewerkt");
            return new OkObjectResult(new { success = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij opslaan thema");
            return new ObjectResult(new { error = "Opslaan mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminThemeExtract")]
    public static async Task<IActionResult> Extract(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/theme/extract")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminThemeExtract");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;

        var url = req.Query["url"].ToString();
        if (string.IsNullOrWhiteSpace(url))
            return new BadRequestObjectResult(new { error = "Parameter 'url' ontbreekt." });

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
            return new BadRequestObjectResult(new { error = "Ongeldige URL. Alleen http/https toegestaan." });

        await PostgresSystemUtilities.WaitForDatabaseAsync(log);
        var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
        var toegestaneHost = await GetToegestaneWebsiteHostAsync(clubCode, log);
        if (toegestaneHost == null || !parsedUri.Host.Equals(toegestaneHost, StringComparison.OrdinalIgnoreCase))
            return new BadRequestObjectResult(new { error = "URL-domein is niet toegestaan. Stel eerst de club-website in via het thema-scherm." });

        try
        {
            var html = await _httpClient.GetStringAsync(parsedUri);
            var colors = ExtractColors(html);
            var faviconUrl = ExtractFaviconUrl(html, parsedUri);
            var logoUrl = ExtractLogoUrl(html, parsedUri);
            log.LogInformation("Assets geëxtraheerd uit {Host}: {Count} kleuren, favicon={Fav}, logo={Logo}",
                parsedUri.Host, colors.Count, faviconUrl != null, logoUrl != null);
            return new OkObjectResult(new { colors, faviconUrl, logoUrl });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Ophalen website mislukt: {Host}", parsedUri.Host);
            return new ObjectResult(new { error = "Website kon niet worden opgehaald." }) { StatusCode = 502 };
        }
    }

    private static async Task<string?> GetToegestaneWebsiteHostAsync(string clubCode, ILogger log)
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT themeclubwebsiteurl FROM public.appsettings WHERE clubcode = @cc", connection);
            cmd.Parameters.AddWithValue("cc", clubCode);
            var result = await cmd.ExecuteScalarAsync();
            var websiteUrl = result as string;
            if (string.IsNullOrWhiteSpace(websiteUrl)) return null;
            return Uri.TryCreate(websiteUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kon toegestane website-host niet bepalen voor extractie");
            return null;
        }
    }

    private static readonly HashSet<string> _skipColors =
        new(StringComparer.OrdinalIgnoreCase)
        { "#ffffff", "#000000", "#eeeeee", "#cccccc", "#f0f0f0", "#333333" };

    private static List<string> ExtractColors(string html)
    {
        var matches = _hexColorRegex.Matches(html);
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var color = m.Value.ToLowerInvariant();
            if (_skipColors.Contains(color)) continue;
            freq[color] = freq.TryGetValue(color, out var c) ? c + 1 : 1;
        }
        return freq.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key).ToList();
    }

    private static bool IsValidHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return _hexColorValidRegex.IsMatch(value);
    }

    private static string? ExtractFaviconUrl(string html, Uri baseUri)
    {
        var m = _faviconRegex.Match(html);
        if (!m.Success) m = _faviconAltRegex.Match(html);
        var href = m.Success ? m.Groups[1].Value : "/favicon.ico";
        return ResolveUrl(href, baseUri);
    }

    private static string? ExtractLogoUrl(string html, Uri baseUri)
    {
        var m = _ogImageRegex.Match(html);
        if (!m.Success) m = _ogImageAltRegex.Match(html);
        if (!m.Success) m = _appleTouchRegex.Match(html);
        if (!m.Success) return null;
        return ResolveUrl(m.Groups[1].Value, baseUri);
    }

    private static string? ResolveUrl(string url, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
            return abs.Scheme == "http" || abs.Scheme == "https" ? abs.ToString() : null;
        if (Uri.TryCreate(baseUri, url, out var rel))
            return rel.Scheme == "http" || rel.Scheme == "https" ? rel.ToString() : null;
        return null;
    }

    private static object DefaultTheme() => new
    {
        primary        = DefaultPrimaryColor,
        secondary      = DefaultSecondaryColor,
        accent         = DefaultAccentColor,
        textOnPrimary  = DefaultTextOnPrimaryColor,
        clubWebsiteUrl = "",
        faviconUrl     = (string?)null,
        logoUrl        = (string?)null
    };
}

internal sealed class ThemeUpdateRequest
{
    public string? Primary        { get; set; }
    public string? Secondary      { get; set; }
    public string? Accent         { get; set; }
    public string? TextOnPrimary  { get; set; }
    public string? ClubWebsiteUrl { get; set; }
    public string? FaviconUrl     { get; set; }
    public string? LogoUrl        { get; set; }
}
