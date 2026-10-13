using Planner.Shared.Infrastructure;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SportlinkFunction.Admin;

/// <summary>
/// Controleert de Easy Auth claims die Azure injecteert via X-MS-CLIENT-PRINCIPAL.
/// Alleen in een lokale ontwikkelomgeving met de expliciete schakelaar <c>LOCAL_AUTH_BYPASS=true</c>
/// altijd toegestaan, en nooit als de app in Azure draait (#1700, <see cref="LokaleOntwikkelomgeving"/>).
/// </summary>
internal static class EasyAuthHelper
{
    private static readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web);

    /// <summary>De beslisregel staat in <see cref="LokaleOntwikkelomgeving"/> (#1700); zonder schakelaar geldt overal de rolcontrole.</summary>
    private static bool LokaleBypass => LokaleOntwikkelomgeving.BypassActief();

    /// <summary>
    /// Controleert of de aanroeper de opgegeven rol(len) heeft.
    /// Returns null als OK, anders een 401/403 result.
    /// </summary>
    public static IActionResult? RequireRole(HttpRequest req, params string[] allowedRoles)
    {
        // Lokale ontwikkelbypass (#1700): alleen met de schakelaar en nooit in Azure.
        if (LokaleBypass)
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

    // RequireAuthenticated ("admin" óf "user") is bij #1350 verwijderd: het werd op geen van beide
    // tiers aangeroepen sinds #310 de teambegeleiding-endpoints admin-only maakte. De rol "user"
    // geeft dus nergens API-toegang — een ongebruikte poort die ruimer is dan alle gebruikte, is
    // een uitnodiging om hem per ongeluk te pakken.

    /// <summary>#988: aanvullende, functionele rol (naast admin/user) voor Sportlink Web
    /// Extension-mutaties (epic #986) — zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §6.
    /// #1376: admin impliceert Wedstrijdzaken — herziening van het eerdere AND-gate-besluit (zie
    /// memory/wedstrijdzaken-rol-vereist-altijd-ook-admin.md). Eén volledige beheerder hoeft geen
    /// aparte tweede Entra-roltoewijzing meer te krijgen om Sportlink-mutaties te mogen
    /// triggeren.</summary>
    public static IActionResult? RequireWedstrijdzaken(HttpRequest req)
        => RequireRole(req, "Wedstrijdzaken", "admin");

    /// <summary>
    /// Niet-gooiende variant van <see cref="RequireRole"/> (#1341) — voor een feature-toggle-check
    /// die zelf al bepaalt wat er bij "nee" gebeurt (bijv. de rol-feature-instelling raadplegen)
    /// in plaats van meteen een 401/403 te retourneren. Zelfde lokale bypass als alle
    /// <c>Require*</c>-methoden: alleen met de schakelaar (#1700) is elke rol "aanwezig".
    /// </summary>
    public static bool IsInRole(HttpRequest req, string role)
    {
        if (LokaleBypass) return true;

        if (!req.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded) ||
            string.IsNullOrEmpty(encoded))
            return false;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded!));
            var principal = JsonSerializer.Deserialize<ClientPrincipal>(json, _opts);
            return principal?.Claims?.Any(c =>
                string.Equals(c.Typ, "roles", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Val, role, StringComparison.OrdinalIgnoreCase)) ?? false;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAdmin(HttpRequest req) => IsInRole(req, "admin");

    private static string? GetClaimValue(HttpRequest req, params string[] claimTypes)
    {
        if (!req.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var encoded) ||
            string.IsNullOrEmpty(encoded))
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded!));
            var principal = JsonSerializer.Deserialize<ClientPrincipal>(json, _opts);
            return principal?.Claims?
                .FirstOrDefault(c => claimTypes.Any(t => string.Equals(c.Typ, t, StringComparison.OrdinalIgnoreCase)))
                ?.Val;
        }
        catch { return null; }
    }

    /// <summary>
    /// Haalt de weergavenaam van de aanroeper op uit de Entra ID claims.
    /// Geeft null terug in lokale ontwikkeling of als de claim ontbreekt.
    /// </summary>
    public static string? GetCallerName(HttpRequest req)
        => GetClaimValue(req, "name");

    /// <summary>
    /// De Entra object-ID (<c>oid</c>) van de aanroeper (#764) — de stabiele, pseudonieme sleutel van
    /// de melder in <c>avg.Feedback</c>. Easy Auth levert afhankelijk van de configuratie de korte
    /// claim <c>oid</c> óf de lange URI-vorm; beide worden herkend. Null in lokale ontwikkeling of als
    /// de claim ontbreekt. Uitsluitend server-side gebruik: nooit in een response of in een publiek issue.
    /// </summary>
    public static string? GetCallerObjectId(HttpRequest req)
        => GetClaimValue(req, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier");

    /// <summary>De aanroeper voor leeracties en hun auditspoor (#1568 deel C): object-ID + naammomentopname, geen e-mailadres.</summary>
    public static global::Planner.Shared.Leren.LerenAanroeper GetLerenAanroeper(HttpRequest req)
        => new(GetCallerObjectId(req), GetCallerName(req)) { HeeftPrincipal = !LokaleBypass };

    /// <summary>
    /// Haalt het e-mailadres van de aanroeper op uit de Entra ID claims.
    /// Uitsluitend voor server-side gebruik (Reply-To in doorstuur-email). Nooit in response terugsturen.
    /// </summary>
    public static string? GetCallerEmail(HttpRequest req)
        => GetClaimValue(req, "preferred_username", "upn", "email");

    /// <summary>
    /// Bepaalt de audit-actor (<c>dbo.AppSettingsAudit.GewijzigdDoor</c>) uitsluitend server-side —
    /// nooit uit client-input (#1003: een geldige admin-token liet de aanroeper voorheen zelf
    /// <c>GewijzigdDoor</c> in de request-body of querystring kiezen, waardoor het auditlog niet
    /// betrouwbaar was voor onderzoek naar misbruik of een gestolen beheerderssessie).
    /// <para>
    /// Zonder lokale bypass (productie, en sinds #1700 ook elke omgeving zonder schakelaar): een stabiele identiteit uit de door Easy Auth
    /// gevalideerde claims — <see cref="GetCallerEmail"/> (upn/preferred_username/email) met
    /// <see cref="GetCallerName"/> als terugval. Ontbreken beide claims tóch (zou nooit mogen na een
    /// geslaagde <see cref="RequireAdmin"/>-check, want Entra levert altijd minimaal één van de twee)
    /// dan gooit dit een <see cref="InvalidOperationException"/> — een productiemutatie mag nooit
    /// stilzwijgend als "onbekend" in het auditlog belanden.
    /// </para>
    /// <para>
    /// Lokale ontwikkeling met <c>LOCAL_AUTH_BYPASS=true</c> buiten Azure (geen Easy Auth-principal beschikbaar):
    /// de herkenbare synthetische testidentiteit <c>"lokale-ontwikkelaar"</c> — nooit "onbekend".
    /// </para>
    /// </summary>
    public static string GetAuditActor(HttpRequest req)
    {
        if (LokaleBypass)
            return "lokale-ontwikkelaar";

        var actor = GetCallerEmail(req) ?? GetCallerName(req);
        if (string.IsNullOrWhiteSpace(actor))
            throw new InvalidOperationException(
                "Audit-actor kon niet worden bepaald: gevalideerde Easy Auth-claims " +
                "(upn/preferred_username/email/name) ontbreken. Mutatie geweigerd om audit-integriteit te waarborgen.");

        return actor;
    }

    private static readonly BekendeClubs Clubs = new(() => BekendeClubs.LeesCodes(
        new Microsoft.Data.SqlClient.SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString),
        "SELECT [ClubCode] FROM [dbo].[AppSettings]"));

    /// <summary>
    /// Eenmaal bij het opstarten: koppelt de geaggregeerde foutlog van de clublijst (#1689) en meldt, zonder
    /// waarden, of de lokale bypass actief is of in Azure is geweigerd (#1700). Die melding gaat naar de
    /// standaarduitvoer: een <c>ILogger</c>-regel buiten een functieaanroep bereikt de Functions-host niet
    /// (vastgesteld met een lokale functiehost), de standaarduitvoer van de worker wel.
    /// </summary>
    public static void KoppelOpstartLogs(Microsoft.Extensions.Logging.ILoggerFactory loggers)
    {
        Clubs.Log = loggers.CreateLogger(nameof(BekendeClubs));
        LokaleOntwikkelomgeving.MeldOpstart(Console.Out);
    }

    /// <summary>
    /// De clubcode voor dit verzoek. De header <c>X-Club-Code</c> is een UX-schakelaar tussen de
    /// primaire club en de democlub, geen autorisatiegrens (#1122, AGENTS.md). De waarde wordt getoetst
    /// aan de bekende clubs (#1677, vorm en bestaan; #1689 begrensd herladen bij een onbekende waarde):
    /// een onbekende of ontbrekende waarde geeft de primaire club; is de lijst nog niet te toetsen, dan
    /// geldt de syntactisch geldige waarde zoals voorheen (<see cref="ClubZoekResultaat.CodeVoorVerzoek"/>).
    /// </summary>
    public static string GetClubCodeFromRequest(HttpRequest req)
        => GetClubCodeFromRequest(req, Clubs, () => SystemUtilities.AppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings"));

    /// <summary>Tier-bedrading met injecteerbare lijst en primaire club; bedoeld voor tests (#1689).</summary>
    internal static string GetClubCodeFromRequest(HttpRequest req, BekendeClubs clubs, Func<string> primaireClub)
        => clubs.ClubCodeVoorVerzoek(req.Headers["X-Club-Code"].ToString(), primaireClub);

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
