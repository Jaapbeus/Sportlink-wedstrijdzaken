using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor het beheren van classificatie-leermomenten. v2 — #323.
///
/// GET /api/beheer/leermomenten?status=pending|validated|rejected&amp;limit=50
/// GET /api/beheer/leermomenten/stats
/// PUT /api/beheer/leermomenten/{id}/valideer  body: { "actie": "valideer" | "afwijzen" }
/// </summary>
public static class AdminLeermomentenFunction
{
    private const int DefaultLimit = 50;
    private const int MaxLimit     = 200;

    [Function("AdminLeermomentenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/leermomenten")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminLeermomentenGet"), "leermomenten ophalen",
            async clubCode =>
            {
                var statusFilter = req.Query["status"].ToString();
                int limit = DefaultLimit;
                if (int.TryParse(req.Query["limit"].ToString(), out var l))
                    limit = Math.Min(MaxLimit, Math.Max(1, l));

                var (count, lim, items) = await AdminLeermomentenRepository.GetAsync(
                    clubCode, statusFilter, limit, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(new { count, limit = lim, items });
            });

    [Function("AdminLeermomentenStats")]
    public static Task<IActionResult> GetStats(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/leermomenten/stats")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminLeermomentenStats"), "leermomenten-stats ophalen",
            async clubCode =>
            {
                var (pending, validated, rejected) = await AdminLeermomentenRepository.GetStatsAsync(
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(new { pending, validated, rejected });
            });

    [Function("AdminLeermomentenValideer")]
    public static Task<IActionResult> Valideer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/leermomenten/{id}/valideer")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminLeermomentenValideer"), "leermoment valideren",
            async clubCode =>
            {
                string body;
                using (var sr = new System.IO.StreamReader(req.Body))
                    body = await sr.ReadToEndAsync();

                string? actie = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("actie", out var a))
                        actie = a.GetString();
                }
                catch { }

                if (actie != "valideer" && actie != "afwijzen")
                    return new BadRequestObjectResult(new { error = "Ongeldige actie. Gebruik 'valideer' of 'afwijzen'." });

                var rows = await AdminLeermomentenRepository.ValideerAsync(
                    id, actie == "valideer", actie == "afwijzen",
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0)
                    return new NotFoundObjectResult(new { error = $"Leermoment {id} niet gevonden." });
                return new OkObjectResult(new { id, actie });
            });
}
