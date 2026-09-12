using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/EasyAuthHelper.cs</c> (#887) — bewuste,
/// vrijwel woordelijke kopie (geen gedeelde abstractie tussen de twee tiers, zie
/// ARCHITECTUUR-DATABASE-TIERS.md §2). De enige inhoudelijke wijziging:
/// <see cref="GetClubCodeFromRequest"/> valt terug op <see cref="PostgresAppSettings"/> in plaats
/// van <c>SystemUtilities.AppSettings</c> — de rest (claims-parsing, correlation-id) is
/// provider-agnostisch en ongewijzigd.
/// </summary>
internal static class EasyAuthHelper
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    private static ClientPrincipal? TryGetPrincipal(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded) ||
            string.IsNullOrEmpty(encoded))
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded!));
            return JsonSerializer.Deserialize<ClientPrincipal>(json, Opts);
        }
        catch { return null; }
    }

    public static IActionResult? RequireRole(HttpRequest req, params string[] allowedRoles)
    {
        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        if (string.IsNullOrEmpty(siteName))
            return null;

        var principal = TryGetPrincipal(req);
        if (principal?.Claims == null)
            return new UnauthorizedResult();

        var hasRole = principal.Claims.Any(c =>
            string.Equals(c.Typ, "roles", StringComparison.OrdinalIgnoreCase) &&
            allowedRoles.Any(r => string.Equals(c.Val, r, StringComparison.OrdinalIgnoreCase)));

        return hasRole
            ? null
            : new ObjectResult(new { error = "Forbidden: vereiste rol ontbreekt" }) { StatusCode = 403 };
    }

    public static IActionResult? RequireAdmin(HttpRequest req) => RequireRole(req, "admin");

    public static IActionResult? RequireAuthenticated(HttpRequest req) => RequireRole(req, "admin", "user");

    // #988: aanvullende, functionele rol (naast admin/user) voor Sportlink Web Extension-mutaties
    // (epic #986) — zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §6.
    public static IActionResult? RequireWedstrijdzaken(HttpRequest req) => RequireRole(req, "Wedstrijdzaken");

    public static string? GetCallerName(HttpRequest req)
    {
        var principal = TryGetPrincipal(req);
        return principal?.Claims?
            .FirstOrDefault(c => string.Equals(c.Typ, "name", StringComparison.OrdinalIgnoreCase))
            ?.Val;
    }

    public static string? GetCallerEmail(HttpRequest req)
    {
        var principal = TryGetPrincipal(req);
        return principal?.Claims?
            .FirstOrDefault(c =>
                string.Equals(c.Typ, "preferred_username", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Typ, "upn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Typ, "email", StringComparison.OrdinalIgnoreCase))
            ?.Val;
    }

    /// <summary>
    /// Bepaalt de audit-actor (<c>public.appsettingsaudit.gewijzigddoor</c>) uitsluitend server-side —
    /// nooit uit client-input (#1003, zelfde precedent als de SQL Server-tier). Zie
    /// <c>FunctionApp/Admin/EasyAuthHelper.cs</c> voor de volledige toelichting.
    /// </summary>
    public static string GetAuditActor(HttpRequest req)
    {
        var siteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        if (string.IsNullOrEmpty(siteName))
            return "lokale-ontwikkelaar";

        var actor = GetCallerEmail(req) ?? GetCallerName(req);
        if (string.IsNullOrWhiteSpace(actor))
            throw new InvalidOperationException(
                "Audit-actor kon niet worden bepaald: gevalideerde Easy Auth-claims " +
                "(upn/preferred_username/email/name) ontbreken. Mutatie geweigerd om audit-integriteit te waarborgen.");

        return actor;
    }

    public static string GetClubCodeFromRequest(HttpRequest req)
    {
        if (req.Headers.TryGetValue("X-Club-Code", out var headerVal) &&
            !string.IsNullOrWhiteSpace(headerVal))
            return headerVal.ToString();

        return PostgresAppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in public.appsettings");
    }

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
