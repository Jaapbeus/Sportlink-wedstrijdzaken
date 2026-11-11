using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Haalt de clubinstellingen op via <c>GET /api/beheer/clubs</c> en retourneert <c>ClubDto[]</c>.
/// Beide tiers gebruiken dezelfde route en responsvorm, zodat de Blazor Admin GUI geen onderscheid
/// hoeft te maken.
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
