using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminEmailLogFunction.cs</c> (#887).
/// Enige verschil: <c>vanaf</c>/<c>tot</c> krijgen <c>DateTime.SpecifyKind(…, Utc)</c> vóórdat ze als
/// parameter naar een <c>TIMESTAMPTZ</c>-kolom gaan — Npgsql weigert een <c>Kind=Unspecified</c>
/// <c>DateTime</c> voor dat type (waar SQL Server's <c>DATETIME2</c> geen Kind kent). Overige logica
/// ongewijzigd.
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
                if (DateTime.TryParse(req.Query["vanaf"].ToString(), out var vd)) vanaf = DateTime.SpecifyKind(vd.Date, DateTimeKind.Utc);
                if (DateTime.TryParse(req.Query["tot"].ToString(),   out var td)) tot   = DateTime.SpecifyKind(td.Date.AddDays(1), DateTimeKind.Utc);
                var statusFilter = req.Query["status"].ToString();
                int limit = DefaultLimit;
                if (int.TryParse(req.Query["limit"].ToString(), out var l))
                    limit = Math.Min(MaxLimit, Math.Max(1, l));

                var items = await AdminEmailLogRepository.GetAsync(
                    clubCode, vanaf, tot, statusFilter, limit, PostgresDatabaseConfig.ConnectionString);
                return new OkObjectResult(new { count = items.Count, limit, items });
            });
}
