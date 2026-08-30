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
    internal static async Task<IActionResult> ExecuteAsync(
        HttpRequest req,
        ILogger log,
        string errorContext,
        Func<string, Task<IActionResult>> work)
    {
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
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
