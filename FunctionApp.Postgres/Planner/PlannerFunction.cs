using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/PlannerFunction.cs</c> (#888). Alleen
/// <c>Veldbezetting</c> is vertaald — de "lichtgewicht wat staat er nu gepland"-weergave (#566)
/// zonder FieldScheduler-berekening. De overige elf planner-endpoints (CheckAvailability,
/// AutoPlan, HerplanCheck, BevestigWedstrijd, GetTeamSchedule, …) hangen af van
/// AvailabilityService/AutoPlanService's FieldScheduler-engine/RescheduleService/
/// TeamScheduleService, die nog niet vertaald zijn — een aanzienlijk grotere, apart te
/// verifiëren stap die buiten deze eerste #888-ronde valt.
/// </summary>
public static class PlannerFunction
{
    [Function("Veldbezetting")]
    public static async Task<IActionResult> Veldbezetting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "planner/veldbezetting")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("Veldbezetting");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            var datumParam = req.Query["datum"].ToString();
            if (string.IsNullOrWhiteSpace(datumParam) || !DateOnly.TryParse(datumParam, out var datum))
                return new BadRequestObjectResult(new { error = "Query parameter 'datum' (yyyy-MM-dd) is verplicht." });

            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            log.LogInformation("Veldbezetting: datum={Datum}, club={Club}", datumParam, clubCode);

            var items = await AutoPlanService.VeldbezettingAsync(
                PostgresDatabaseConfig.ConnectionString, datum, clubCode);
            return new OkObjectResult(items);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Veldbezetting failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }
}
