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
}
