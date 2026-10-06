using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Planner.Endpoints.Leren;

namespace SportlinkFunction.Admin;

/// <summary>
/// Wachtrij met teamteksten die de e-mailpipeline niet kon koppelen (#1568 deel C). Alleen rol <c>admin</c>;
/// alle orkestratie staat in <c>Planner.Endpoints/Leren/OnbekendeTeamTekstEndpointCore</c>. Postgres-tegenhanger:
/// <c>FunctionApp.Postgres/Admin/AdminOnbekendeTeamTekstFunction.cs</c>. Koppelen aan een team loopt via
/// <c>POST /api/beheer/teamaliassen</c>, dat de open regel zelf op afgehandeld zet.
///
/// GET /api/beheer/onbekende-teamteksten?status=open|afgehandeld|genegeerd&amp;limit=100
/// PUT /api/beheer/onbekende-teamteksten/{id}/status  body: { "status": "open" | "afgehandeld" | "genegeerd" }
/// </summary>
public static class AdminOnbekendeTeamTekstFunction
{
    private static SqlOnbekendeTeamTekstStore Opslag() => new(SystemUtilities.DatabaseConfig.ConnectionString);

    [Function("AdminOnbekendeTeamTekstGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/onbekende-teamteksten")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminOnbekendeTeamTekstGet"), "onbekende teamteksten ophalen",
            clubCode => OnbekendeTeamTekstEndpointCore.LijstAsync(clubCode, req.Query, Opslag()));

    [Function("AdminOnbekendeTeamTekstStatus")]
    public static Task<IActionResult> ZetStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/onbekende-teamteksten/{id:int}/status")] HttpRequest req,
        int id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminOnbekendeTeamTekstStatus"), "onbekende teamtekst bijwerken",
            async clubCode => await OnbekendeTeamTekstEndpointCore.ZetStatusAsync(
                clubCode, id, await TeamAliasEndpointCore.LeesBodyAsync(req), Opslag()));
}
