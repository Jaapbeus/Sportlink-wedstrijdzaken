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
    // Clubs-endpoint gebruikt geen clubCode-filter; bevraagt alle AppSettings.
    // AdminEndpoint wrapper niet gebruikt omdat GetClubCodeFromRequest hier niet van toepassing is.
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
            await SystemUtilities.WaitForDatabaseAsync(log);
            var clubs = await AdminClubsRepository.GetClubsAsync(SystemUtilities.DatabaseConfig.ConnectionString);
            return new OkObjectResult(clubs);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij ophalen clubs");
            return new ObjectResult(new { error = "Ophalen mislukt" }) { StatusCode = 500 };
        }
    }
}
