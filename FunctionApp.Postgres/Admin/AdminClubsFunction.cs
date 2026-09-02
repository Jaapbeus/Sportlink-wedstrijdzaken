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
    [Function("AdminClubsGet")]
    public static async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/clubs")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminClubsGet");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);
            var clubs = await AdminClubsRepository.GetClubsAsync(PostgresDatabaseConfig.ConnectionString);
            return new OkObjectResult(clubs);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen clubs");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }
}
