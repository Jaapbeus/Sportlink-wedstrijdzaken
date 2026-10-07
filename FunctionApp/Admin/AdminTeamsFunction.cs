using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace SportlinkFunction.Admin;

/// <summary>
/// Levert de lijst van canonieke teamnamen uit dbo.Teams (#756) — één rij per fysiek team,
/// ontdubbeld en genormaliseerd door TeamCanonicalisatieService. Gebruikt door de Blazor Admin UI
/// voor dropdowns in voorkeurstijden en teamregels.
/// </summary>
public static class AdminTeamsFunction
{
    [Function("AdminTeamsGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teams")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamsGet"), "teams ophalen",
            async clubCode =>
            {
                var teams = await AdminTeamsRepository.GetTeamnamenAsync(
                    clubCode, SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(teams);
            });

    /// <summary>Keuzelijst met teamId + canonieke naam (#1568 deel C), voor het koppelen van een teamtekst aan een team.</summary>
    [Function("AdminTeamsKeuzelijst")]
    public static Task<IActionResult> Keuzelijst(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/teams/keuzelijst")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminTeamsKeuzelijst"), "teamkeuzelijst ophalen",
            async clubCode => new OkObjectResult(await AdminTeamsRepository.GetKeuzelijstAsync(
                clubCode, SystemUtilities.DatabaseConfig.ConnectionString)));
}
