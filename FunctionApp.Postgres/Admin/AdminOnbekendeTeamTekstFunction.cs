using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Planner.Endpoints.Leren;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Wachtrij met teamteksten die de e-mailpipeline niet kon koppelen (#1568 deel C) — Postgres-tier-tegenhanger
/// van <c>FunctionApp/Admin/AdminOnbekendeTeamTekstFunction.cs</c>. Alleen rol <c>admin</c>; alle orkestratie
/// staat in <c>Planner.Endpoints/Leren/OnbekendeTeamTekstEndpointCore</c>. Koppelen aan een team loopt via
/// <c>POST /api/beheer/teamaliassen</c>, dat de open regel zelf op afgehandeld zet.
/// </summary>
public static class AdminOnbekendeTeamTekstFunction
{
    private static PostgresOnbekendeTeamTekstStore Opslag() => new(PostgresDatabaseConfig.ConnectionString);

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
