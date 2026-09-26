using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// Standaard wrapper voor admin-endpoints. Garandeert:
/// - RequireAdmin guard (auth check — altijd als eerste)
/// - CorrelationId-scope voor request-tracing
/// - WaitForDatabase readiness check
/// - Uniforme 500-fallback
///
/// Gebruik: return await AdminEndpoint.ExecuteAsync(req, log, "context", async clubCode => { ... });
/// Specifieke exceptions (SqlException conflict etc.) worden BINNEN de delegate afgehandeld. (#467)
/// <para>
/// <b>Sinds #1350 de enige autorisatiepoort voor admin-endpoints op deze tier.</b> Elk
/// HTTP-endpoint met de admin-rol loopt via <see cref="ExecuteAsync"/> of
/// <see cref="ExecuteZonderDatabaseAsync"/>. Een losse <c>EasyAuthHelper.RequireAdmin</c>-aanroep
/// in een endpoint wordt door <c>scripts/ci/check-endpoint-autorisatie.sh</c> geweigerd, en
/// <c>FunctionApp.Tests/Admin/EndpointAutorisatieTests.cs</c> bewijst per endpoint dat de poort
/// dicht zit zonder rol en open gaat mét rol.
/// </para>
/// </summary>
internal static class AdminEndpoint
{
    /// <summary>
    /// Testhaak (#1350) — uitsluitend bereikbaar via <c>InternalsVisibleTo</c>. Staat hij, dan keert
    /// elke aanroep die de autorisatiepoort passeert meteen terug met het resultaat van de haak: vóór
    /// de databasewacht en vóór het werk van het endpoint. Daarmee kan de autorisatietest per
    /// endpoint bewijzen dat een admin de poort passeert én dat het endpoint via deze wrapper loopt —
    /// zonder database en zonder neveneffect (een DELETE- of sync-endpoint mag in een test nooit
    /// echt werk doen; in de CI-job met een levende database zou dat anders wél gebeuren).
    /// De haak zit ná de poort en kan die dus nooit verzwakken; productie heeft geen pad dat hem zet.
    /// </summary>
    internal static Func<string, IActionResult>? PoortGepasseerdVoorTests;

    internal static async Task<IActionResult> ExecuteAsync(
        HttpRequest req,
        ILogger log,
        string errorContext,
        Func<string, Task<IActionResult>> work)
    {
        var (correlationId, authResult) = Poort(req);
        if (authResult != null) return authResult;
        if (PoortGepasseerdVoorTests is { } haak) return haak(errorContext);

        using var _ = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            return await work(clubCode);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Context} mislukt [correlationId={CorrelationId}]", errorContext, correlationId);
            return new ObjectResult(new { error = "Interne fout" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Zelfde poort als <see cref="ExecuteAsync"/>, zonder databasewacht en zonder clubcode — voor
    /// endpoints die geen database nodig hebben (#1350: de feedback-endpoints, die juist moeten
    /// blijven werken als de database onbereikbaar is). Bewust een aparte methode en géén optionele
    /// parameter op <see cref="ExecuteAsync"/>: een vlag die stilzwijgend een stap overslaat is
    /// dezelfde valkuil als de <c>requireRole</c>-parameter die #1272 verwijderde.
    /// </summary>
    internal static async Task<IActionResult> ExecuteZonderDatabaseAsync(
        HttpRequest req,
        ILogger log,
        string errorContext,
        Func<Task<IActionResult>> work)
    {
        var (correlationId, authResult) = Poort(req);
        if (authResult != null) return authResult;
        if (PoortGepasseerdVoorTests is { } haak) return haak(errorContext);

        using var _ = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Context} mislukt [correlationId={CorrelationId}]", errorContext, correlationId);
            return new ObjectResult(new { error = "Interne fout" }) { StatusCode = 500 };
        }
    }

    // De ene plek waar de poort staat. #1272: de optionele requireRole-parameter van #991 is hier
    // weg. Die verving de admin-controle in plaats van er bovenop te komen, wat §3.4 van
    // docs/SPORTLINK-WEB-EXTENSION.md tegensprak. De Sportlink-endpoints gebruiken nu
    // SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync, dat beide poorten na elkaar zet — net
    // als de Postgres-tier. De parameter is verwijderd en niet alleen ongebruikt gelaten: een
    // optionele parameter die stilzwijgend een autorisatiecontrole vervangt, is een valkuil die
    // vanzelf een tweede keer gebruikt wordt.
    private static (string CorrelationId, IActionResult? AuthResult) Poort(HttpRequest req)
    {
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        return (correlationId, EasyAuthHelper.RequireAdmin(req));
    }
}
