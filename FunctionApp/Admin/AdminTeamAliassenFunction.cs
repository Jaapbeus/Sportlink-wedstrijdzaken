using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor het valideren van teamnaam-aliassen (#701, onderdeel van #692).
///
/// Een alias wordt door de sync of door de AI-disambiguatie vastgelegd als <c>pending</c>.
/// Alleen aliassen die een coördinator hier goedkeurt (status <c>validated</c>) mogen bij
/// teamnaam-resolutie als vertrouwde exacte match gelden — zo kan een foutieve
/// AI-disambiguatie of typefout zich niet zelfversterken.
///
/// GET    /api/beheer/teamaliassen?status=pending|validated|rejected&amp;limit=50
/// PUT    /api/beheer/teamaliassen/{id}/valideer   body: { "status": "validated" | "rejected" }
/// DELETE /api/beheer/teamaliassen/{id}
/// </summary>
public static class AdminTeamAliassenFunction
{
    private const int DefaultLimit = 100;
    private const int MaxLimit     = 500;

    [Function("AdminTeamAliassenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teamaliassen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenGet"), "teamaliassen ophalen",
            async clubCode =>
            {
                var statusFilter = req.Query["status"].ToString();
                if (!string.IsNullOrWhiteSpace(statusFilter) &&
                    !AdminTeamAliassenRepository.GeldigeStatussen.Contains(statusFilter))
                    return new BadRequestObjectResult(new
                    {
                        error = "Ongeldige status. Gebruik 'pending', 'validated' of 'rejected'."
                    });

                int limit = DefaultLimit;
                if (int.TryParse(req.Query["limit"].ToString(), out var l))
                    limit = Math.Min(MaxLimit, Math.Max(1, l));

                var cs = SystemUtilities.DatabaseConfig.ConnectionString;
                try
                {
                    var (count, lim, items) = await AdminTeamAliassenRepository.GetAsync(
                        clubCode, statusFilter, limit, cs);
                    var (pending, validated, rejected) = await AdminTeamAliassenRepository.GetStatsAsync(clubCode, cs);
                    return new OkObjectResult(new { count, limit = lim, pending, validated, rejected, items });
                }
                catch (SqlException ex) when (ex.Number == 208)
                {
                    // Tabel bestaat nog niet — post-deployment script nog niet uitgevoerd.
                    return new OkObjectResult(new
                    {
                        count = 0, limit, pending = 0, validated = 0, rejected = 0,
                        items = new List<object>()
                    });
                }
            });

    [Function("AdminTeamAliassenValideer")]
    public static Task<IActionResult> Valideer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/teamaliassen/{id:int}/valideer")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenValideer"), "teamalias valideren",
            async clubCode =>
            {
                string body;
                using (var sr = new System.IO.StreamReader(req.Body))
                    body = await sr.ReadToEndAsync();

                string? status = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("status", out var s))
                        status = s.GetString();
                }
                catch { /* ongeldige JSON → valt hieronder in de 400 */ }

                if (status != "validated" && status != "rejected")
                    return new BadRequestObjectResult(new
                    {
                        error = "Ongeldige status. Gebruik 'validated' of 'rejected'."
                    });

                var rows = await AdminTeamAliassenRepository.ZetStatusAsync(
                    id, status, clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0)
                    return new NotFoundObjectResult(new { error = $"Teamalias {id} niet gevonden." });
                return new OkObjectResult(new { id, status });
            });

    [Function("AdminTeamAliassenDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/teamaliassen/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenDelete"), "teamalias verwijderen",
            async clubCode =>
            {
                var rows = await AdminTeamAliassenRepository.DeleteAsync(
                    id, clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                if (rows == 0)
                    return new NotFoundObjectResult(new { error = $"Teamalias {id} niet gevonden." });
                return new OkObjectResult(new { deleted = true, id });
            });
}
