using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// Geeft de lijst van beschikbare clubs terug (uit dbo.AppSettings).
/// Gebruikt door de Blazor Admin UI voor de club-selector dropdown in de topbalk.
///
/// GET /api/beheer/clubs → ClubDto[]
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
                var clubs = await AdminClubsRepository.GetClubsAsync(SystemUtilities.DatabaseConfig.ConnectionString);
                return new OkObjectResult(clubs);
            });
}
