using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SportlinkFunction.Admin;

/// <summary>
/// Controleert de Easy Auth claims die Azure injecteert via X-MS-CLIENT-PRINCIPAL.
/// In lokale ontwikkeling (geen WEBSITE_SITE_NAME) altijd toegestaan.
/// </summary>
internal static class EasyAuthHelper
{
    private static readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Controleert of de aanroeper de opgegeven rol(len) heeft.
    /// Returns null als OK, anders een 401/403 result.
    /// </summary>
    public static IActionResult? RequireRole(HttpRequest req, params string[] allowedRoles)
    {
        // Lokale ontwikkeling: geen Easy Auth — altijd toestaan
        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        if (string.IsNullOrEmpty(siteName))
            return null;

        if (!req.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded) ||
            string.IsNullOrEmpty(encoded))
            return new UnauthorizedResult();

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded!));
            var principal = JsonSerializer.Deserialize<ClientPrincipal>(json, _opts);
            if (principal?.Claims == null)
                return new UnauthorizedResult();

            var hasRole = principal.Claims.Any(c =>
                string.Equals(c.Typ, "roles", StringComparison.OrdinalIgnoreCase) &&
                allowedRoles.Any(r => string.Equals(c.Val, r, StringComparison.OrdinalIgnoreCase)));

            return hasRole
                ? null
                : new ObjectResult(new { error = $"Forbidden: vereiste rol ontbreekt" }) { StatusCode = 403 };
        }
        catch
        {
            return new UnauthorizedResult();
        }
    }

    /// <summary>Controleert 'admin' rol. Delegeert naar RequireRole.</summary>
    public static IActionResult? RequireAdmin(HttpRequest req)
        => RequireRole(req, "admin");

    /// <summary>Controleert 'admin' of 'user' rol. Delegeert naar RequireRole.</summary>
    public static IActionResult? RequireAuthenticated(HttpRequest req)
        => RequireRole(req, "admin", "user");

    /// <summary>
    /// Haalt de weergavenaam van de aanroeper op uit de Entra ID claims.
    /// Geeft null terug in lokale ontwikkeling of als de claim ontbreekt.
    /// </summary>
    public static string? GetCallerName(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded) ||
            string.IsNullOrEmpty(encoded))
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded!));
            var principal = JsonSerializer.Deserialize<ClientPrincipal>(json, _opts);
            return principal?.Claims?
                .FirstOrDefault(c => string.Equals(c.Typ, "name", StringComparison.OrdinalIgnoreCase))
                ?.Val;
        }
        catch { return null; }
    }

    /// <summary>
    /// Haalt het e-mailadres van de aanroeper op uit de Entra ID claims.
    /// Uitsluitend voor server-side gebruik (Reply-To in doorstuur-email). Nooit in response terugsturen.
    /// </summary>
    public static string? GetCallerEmail(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded) ||
            string.IsNullOrEmpty(encoded))
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded!));
            var principal = JsonSerializer.Deserialize<ClientPrincipal>(json, _opts);
            return principal?.Claims?
                .FirstOrDefault(c =>
                    string.Equals(c.Typ, "preferred_username", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Typ, "upn", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Typ, "email", StringComparison.OrdinalIgnoreCase))
                ?.Val;
        }
        catch { return null; }
    }

    /// <summary>
    /// Leest de club-code uit de X-Club-Code request header.
    /// Terugval op ClubCode uit dbo.AppSettings als de header ontbreekt.
    /// </summary>
    public static string GetClubCodeFromRequest(HttpRequest req)
    {
        if (req.Headers.TryGetValue("X-Club-Code", out var headerVal) &&
            !string.IsNullOrWhiteSpace(headerVal))
            return headerVal.ToString();

        return SystemUtilities.AppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");
    }

    /// <summary>
    /// Leest x-correlation-id uit de request header, of genereert een nieuwe GUID.
    /// Schrijft het ID terug in de response header voor end-to-end tracing.
    /// </summary>
    public static string ExtractOrCreateCorrelationId(HttpRequest req)
    {
        var correlationId = req.Headers.TryGetValue("x-correlation-id", out var incoming) && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Guid.NewGuid().ToString("N");

        req.HttpContext.Response.Headers["x-correlation-id"] = correlationId;
        return correlationId;
    }

    private sealed class ClientPrincipal
    {
        [JsonPropertyName("claims")]
        public List<ClaimEntry>? Claims { get; set; }
    }

    private sealed class ClaimEntry
    {
        [JsonPropertyName("typ")]
        public string Typ { get; set; } = "";

        [JsonPropertyName("val")]
        public string Val { get; set; } = "";
    }
}
