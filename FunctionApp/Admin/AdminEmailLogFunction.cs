using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Planner.Endpoints.Admin;
using SportlinkFunction.Email;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor email-verwerkingslog. v2 — #93.
///
/// GET /api/beheer/email-log?vanaf=YYYY-MM-DD&amp;tot=YYYY-MM-DD&amp;status=X&amp;limit=50
///
/// AVG: NOOIT EmailBody of AntwoordEmail teruggeven; alleen metadata.
/// </summary>
public static class AdminEmailLogFunction
{
    [Function("AdminEmailLogGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/email-log")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminEmailLogGet"), "email-log ophalen",
            async clubCode =>
            {
                var filter = EmailLogEndpointCore.LeesFilter(req.Query);
                var items = await AdminEmailLogRepository.GetAsync(
                    clubCode, filter.Vanaf, filter.Tot, filter.Status, filter.Limit, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(new { count = items.Count, limit = filter.Limit, items });
            });

    /// <summary>
    /// GET /api/beheer/email-log/{id}/trace — PII-arme beslissingstrace van één verwerking (#1568).
    /// Bevat nooit body, afzender of onderwerp; blijft beschikbaar nadat de verwerking is opgeruimd.
    /// </summary>
    [Function("AdminEmailLogTraceGet")]
    public static Task<IActionResult> GetTrace(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/email-log/{id}/trace")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminEmailLogTraceGet"), "email-trace ophalen",
            async clubCode =>
            {
                var trace = await EmailTraceRepository.HaalOpAsync(SystemUtilities.DatabaseConfig.ConnectionString, clubCode, id);
                return EmailLogEndpointCore.VertaalTrace(trace);
            });
}
