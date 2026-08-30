using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminTeamsFunction.cs</c> (#887).
/// Bewuste kopie — geen logicawijziging t.o.v. de SQL Server-tier.
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
                    clubCode, PostgresDatabaseConfig.ConnectionString);
                return new OkObjectResult(teams);
            });
}
