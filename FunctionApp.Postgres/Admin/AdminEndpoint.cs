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
        Func<string, Task<IActionResult>> work)
    {
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        // #1272: de optionele requireRole-parameter van #991 is hier weg. Die verving de
        // admin-controle in plaats van er bovenop te komen, wat §3.4 van
        // docs/SPORTLINK-WEB-EXTENSION.md tegensprak. De Sportlink-endpoints gebruiken nu
        // SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync, dat beide poorten na elkaar zet —
        // net als de SQL Server-tier. De parameter is verwijderd en niet alleen ongebruikt
        // gelaten: een optionele parameter die stilzwijgend een autorisatiecontrole vervangt, is
        // een valkuil die vanzelf een tweede keer gebruikt wordt.
        var authResult = EasyAuthHelper.RequireAdmin(req);
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
