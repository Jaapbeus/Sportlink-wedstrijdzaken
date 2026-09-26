using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminClubsFunction.cs</c> (#887).
/// GET /api/beheer/clubs → ClubDto[]. Zelfde route, zelfde vorm — de Blazor Admin GUI onderscheidt
/// geen tier.
/// </summary>
public static class AdminClubsFunction
{
    // Bevraagt alle clubs, dus de clubcode uit de wrapper wordt hier bewust genegeerd (#1350).
    [Function("AdminClubsGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/clubs")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminClubsGet"), "clubs ophalen",
            async _ =>
            {
                var clubs = await AdminClubsRepository.GetClubsAsync(PostgresDatabaseConfig.ConnectionString);
                return new OkObjectResult(clubs);
            });
}
