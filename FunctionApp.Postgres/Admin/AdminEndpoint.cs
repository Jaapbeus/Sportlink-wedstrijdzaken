using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminEndpoint.cs</c> (#887) — bewuste kopie
/// (geen gedeelde abstractie, zie ARCHITECTUUR-DATABASE-TIERS.md §2). Enige wijziging:
/// <c>SystemUtilities.WaitForDatabaseAsync</c> → <see cref="PostgresSystemUtilities.WaitForDatabaseAsync"/>.
/// </summary>
internal static class AdminEndpoint
{
    internal const int OutboundHttpTimeoutSeconds = 10;
    internal const string OutboundUserAgent = "SportlinkAdmin/2.0";

    internal static async Task<IActionResult> ExecuteAsync(
        HttpRequest req,
        ILogger log,
        string errorContext,
        Func<string, Task<IActionResult>> work,
        Func<HttpRequest, IActionResult?>? requireRole = null)
    {
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        // #991: optioneel overschrijfbaar — default blijft RequireAdmin, dus 100%
        // backward-compatible voor alle bestaande aanroepen. Eerste afnemer van een andere waarde:
        // SportlinkMatchFunction (RequireWedstrijdzaken), zie #988 Besluit 1.
        var authResult = (requireRole ?? EasyAuthHelper.RequireAdmin)(req);
        if (authResult != null) return authResult;

        using var _ = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            return await work(clubCode);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Context} mislukt [correlationId={CorrelationId}]", errorContext, correlationId);
            return new ObjectResult(new { error = "Interne fout" }) { StatusCode = 500 };
        }
    }
}
