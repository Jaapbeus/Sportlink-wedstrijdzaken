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
    /// Controleert of de aanroeper geauthenticeerd is en de 'admin' rol heeft.
    /// Returns null als OK, anders een 401/403 result.
    /// </summary>
    public static IActionResult? RequireAdmin(HttpRequest req)
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

            var hasAdmin = principal.Claims.Any(c =>
                string.Equals(c.Typ, "roles", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Val, "admin", StringComparison.OrdinalIgnoreCase));

            return hasAdmin
                ? null
                : new ObjectResult(new { error = "Forbidden: admin role required" }) { StatusCode = 403 };
        }
        catch
        {
            return new UnauthorizedResult();
        }
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
