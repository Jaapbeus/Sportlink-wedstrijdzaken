using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor club-thema beheer. v2 — #325.
///
/// GET  /api/beheer/theme              → haal huidige themakleuren op
/// PUT  /api/beheer/theme              → sla themakleuren op
/// POST /api/beheer/theme/extract?url= → extraheer kleuren uit club-website (SSRF-beschermd)
/// </summary>
public static class AdminThemeFunction
{
    private static readonly HttpClient _httpClient;
    private static readonly Regex _hexColorRegex = new(@"#([0-9a-fA-F]{6})\b", RegexOptions.Compiled);
    private static readonly Regex _hexColorValidRegex = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    static AdminThemeFunction()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SportlinkAdmin/2.0");
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
            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                SELECT [ThemeColorPrimary], [ThemeColorSecondary], [ThemeColorAccent],
                       [ThemeColorTextOnPrimary], [ThemeClubWebsiteUrl]
                FROM [dbo].[AppSettings]", connection);
            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return new OkObjectResult(DefaultTheme());

            return new OkObjectResult(new
            {
                primary        = reader.IsDBNull(0) ? "#1b6ec2" : reader.GetString(0),
                secondary      = reader.IsDBNull(1) ? "#6c757d" : reader.GetString(1),
                accent         = reader.IsDBNull(2) ? "#0071c1" : reader.GetString(2),
                textOnPrimary  = reader.IsDBNull(3) ? "#ffffff" : reader.GetString(3),
                clubWebsiteUrl = reader.IsDBNull(4) ? ""        : reader.GetString(4)
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

            await SystemUtilities.WaitForDatabaseAsync(log);
            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(@"
                UPDATE [dbo].[AppSettings]
                SET [ThemeColorPrimary]        = @Primary,
                    [ThemeColorSecondary]      = @Secondary,
                    [ThemeColorAccent]         = @Accent,
                    [ThemeColorTextOnPrimary]  = @TextOnPrimary,
                    [ThemeClubWebsiteUrl]      = @WebsiteUrl", connection);
            command.Parameters.AddWithValue("@Primary",        dto.Primary       ?? "#1b6ec2");
            command.Parameters.AddWithValue("@Secondary",      dto.Secondary     ?? "#6c757d");
            command.Parameters.AddWithValue("@Accent",         dto.Accent        ?? "#0071c1");
            command.Parameters.AddWithValue("@TextOnPrimary",  dto.TextOnPrimary ?? "#ffffff");
            command.Parameters.AddWithValue("@WebsiteUrl",     (object?)dto.ClubWebsiteUrl ?? DBNull.Value);
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

        // SSRF-bescherming: blokkeer privé-IPs en interne adressen
        if (await IsPrivateOrReservedAsync(parsedUri.Host))
            return new BadRequestObjectResult(new { error = "Privé-IP's en interne adressen zijn niet toegestaan." });

        try
        {
            var html = await _httpClient.GetStringAsync(parsedUri);
            var colors = ExtractColors(html);
            log.LogInformation("Kleuren geëxtraheerd uit {Host}: {Count} gevonden", parsedUri.Host, colors.Count);
            return new OkObjectResult(new { colors });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Ophalen website mislukt: {Host}", parsedUri.Host);
            return new ObjectResult(new { error = "Website kon niet worden opgehaald." }) { StatusCode = 502 };
        }
    }

    private static async Task<bool> IsPrivateOrReservedAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            return addresses.Any(IsPrivateOrReserved);
        }
        catch
        {
            return true; // DNS-fout → blokkeer
        }
    }

    private static bool IsPrivateOrReserved(IPAddress addr)
    {
        var bytes = addr.GetAddressBytes();
        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (bytes[0] == 127) return true;                               // 127.0.0.0/8 loopback
            if (bytes[0] == 10) return true;                                // 10.0.0.0/8 RFC1918
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true; // 172.16.0.0/12 RFC1918
            if (bytes[0] == 192 && bytes[1] == 168) return true;           // 192.168.0.0/16 RFC1918
            if (bytes[0] == 169 && bytes[1] == 254) return true;           // 169.254.0.0/16 link-local
        }
        else if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (addr.Equals(IPAddress.IPv6Loopback)) return true;           // ::1
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true; // fe80::/10 link-local
            if ((bytes[0] & 0xfe) == 0xfc) return true;                    // fc00::/7 unique local
        }
        return false;
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

    private static object DefaultTheme() => new
    {
        primary        = "#1b6ec2",
        secondary      = "#6c757d",
        accent         = "#0071c1",
        textOnPrimary  = "#ffffff",
        clubWebsiteUrl = ""
    };
}

internal sealed class ThemeUpdateRequest
{
    public string? Primary       { get; set; }
    public string? Secondary     { get; set; }
    public string? Accent        { get; set; }
    public string? TextOnPrimary { get; set; }
    public string? ClubWebsiteUrl { get; set; }
}
