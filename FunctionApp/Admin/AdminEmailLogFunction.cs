using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

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
    private const int DefaultLimit = 50;
    private const int MaxLimit     = 200;

    [Function("AdminEmailLogGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/email-log")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminEmailLogGet"), "email-log ophalen",
            async clubCode =>
            {
                DateTime? vanaf = null, tot = null;
                if (DateTime.TryParse(req.Query["vanaf"].ToString(), out var vd)) vanaf = vd.Date;
                if (DateTime.TryParse(req.Query["tot"].ToString(),   out var td)) tot   = td.Date.AddDays(1);
                var statusFilter = req.Query["status"].ToString();
                int limit = DefaultLimit;
                if (int.TryParse(req.Query["limit"].ToString(), out var l))
                    limit = Math.Min(MaxLimit, Math.Max(1, l));

                var items = await AdminEmailLogRepository.GetAsync(
                    clubCode, vanaf, tot, statusFilter, limit, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(new { count = items.Count, limit, items });
            });
}
