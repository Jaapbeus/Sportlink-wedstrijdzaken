using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Planner.Endpoints.Leren;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor het beheren van classificatie-leermomenten. v2 — #323.
///
/// GET /api/beheer/leermomenten?status=pending|validated|rejected&amp;limit=50
/// GET /api/beheer/leermomenten/stats
/// PUT /api/beheer/leermomenten/{id}/valideer  body: { "actie": "valideer" | "afwijzen" }
/// POST /api/beheer/leermomenten  (#1568 deel C) — leermoment door een beheerder: direct gevalideerd, herkomst Admin
/// </summary>
public static class AdminLeermomentenFunction
{
    [Function("AdminLeermomentenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/leermomenten")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminLeermomentenGet"), "leermomenten ophalen",
            async clubCode =>
            {
                var (statusFilter, limit) = LeermomentEndpointCore.LeesLijstFilter(req.Query);
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

    /// <summary>Een leermoment door een beheerder (#1568 deel C): direct gevalideerd, herkomst <c>Admin</c>, permanent.</summary>
    [Function("AdminLeermomentenPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/leermomenten")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminLeermomentenPost"), "leermoment toevoegen",
            async clubCode => await LeermomentEndpointCore.AanmakenAsync(
                clubCode, await TeamAliasEndpointCore.LeesBodyAsync(req), EasyAuthHelper.GetLerenAanroeper(req),
                o => AdminLeermomentenRepository.MaakAdminLeermomentAsync(o, SystemUtilities.DatabaseConfig.ConnectionString)));

    [Function("AdminLeermomentenValideer")]
    public static Task<IActionResult> Valideer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/leermomenten/{id}/valideer")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminLeermomentenValideer"), "leermoment valideren",
            async clubCode => await LeermomentEndpointCore.ValideerAsync(
                id, await TeamAliasEndpointCore.LeesBodyAsync(req),
                (leermomentId, isGevalideerd, isAfgewezen) => AdminLeermomentenRepository.ValideerAsync(
                    leermomentId, isGevalideerd, isAfgewezen, clubCode, SystemUtilities.DatabaseConfig.ConnectionString)));

    /// <summary>Verwijdert een door een beheerder toegevoegd leermoment (AVG); een leermoment uit een beantwoorde mail geeft 409.</summary>
    [Function("AdminLeermomentenDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "beheer/leermomenten/{id:int}")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminLeermomentenDelete"), "leermoment verwijderen",
            clubCode => LeermomentEndpointCore.VerwijderAsync(
                id, leermomentId => AdminLeermomentenRepository.VerwijderAdminLeermomentAsync(leermomentId, clubCode, SystemUtilities.DatabaseConfig.ConnectionString)));
}
