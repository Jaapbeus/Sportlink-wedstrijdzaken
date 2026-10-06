using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Planner.Endpoints.Leren;

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
    [Function("AdminTeamAliassenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teamaliassen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenGet"), "teamaliassen ophalen",
            async clubCode =>
            {
                var (statusFilter, limit, fout) = TeamAliasEndpointCore.LeesLijstFilter(req.Query);
                if (fout is not null) return fout;

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

    private static SqlTeamAliasStore AliasStore() => new(SystemUtilities.DatabaseConfig.ConnectionString);
    private static SqlOnbekendeTeamTekstStore Wachtrij() => new(SystemUtilities.DatabaseConfig.ConnectionString);

    /// <summary>Aanmaken door een beheerder (#1568 deel C): bron <c>CoordinatorCorrectie</c>, direct <c>validated</c>.</summary>
    [Function("AdminTeamAliassenPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/teamaliassen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenPost"), "teamalias aanmaken",
            async clubCode => await TeamAliasEndpointCore.AanmakenAsync(
                clubCode, await TeamAliasEndpointCore.LeesBodyAsync(req), EasyAuthHelper.GetLerenAanroeper(req),
                AliasStore(), Wachtrij(), context.GetLogger("AdminTeamAliassenPost")));

    [Function("AdminTeamAliassenValideer")]
    public static Task<IActionResult> Valideer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/teamaliassen/{id:int}/valideer")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenValideer"), "teamalias valideren",
            async clubCode => await TeamAliasEndpointCore.ValideerAsync(
                id, await TeamAliasEndpointCore.LeesBodyAsync(req), EasyAuthHelper.GetLerenAanroeper(req),
                (aliasId, status, wie) => AdminTeamAliassenRepository.ZetStatusAsync(
                    aliasId, status, clubCode, wie, SystemUtilities.DatabaseConfig.ConnectionString)));

    [Function("AdminTeamAliassenDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/teamaliassen/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamAliassenDelete"), "teamalias verwijderen",
            clubCode => TeamAliasEndpointCore.VerwijderAsync(
                id, EasyAuthHelper.GetLerenAanroeper(req), context.GetLogger("AdminTeamAliassenDelete"),
                aliasId => AdminTeamAliassenRepository.DeleteAsync(aliasId, clubCode, SystemUtilities.DatabaseConfig.ConnectionString)));
}
