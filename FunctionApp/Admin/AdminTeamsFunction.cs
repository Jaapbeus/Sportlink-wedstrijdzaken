using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace SportlinkFunction.Admin;

/// <summary>
/// Levert de lijst van teams uit his.teams (gesynchroniseerd via Sportlink API), gefilterd op ClubCode.
/// Gebruikt door de Blazor Admin UI voor dropdowns in voorkeurstijden en teamregels.
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
}
