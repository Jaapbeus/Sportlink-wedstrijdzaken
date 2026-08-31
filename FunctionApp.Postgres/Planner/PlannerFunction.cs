using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/PlannerFunction.cs</c> (#888). Vertaald
/// zijn <c>Veldbezetting</c> — de "lichtgewicht wat staat er nu gepland"-weergave (#566) zonder
/// FieldScheduler-berekening — en <c>GetTeamSchedule</c>, het teamrooster.
/// <para>
/// De overige tien planner-endpoints (CheckAvailability, DoordeweeksBeschikbaar, AutoPlan,
/// AutoPlanToepassen, HerplanCheck, HerplanBevestig, BevestigWedstrijd, ZoekWedstrijd,
/// PopulateSunset) hangen af van <c>AvailabilityService</c>, de FieldScheduler-engine in
/// <c>PlannerShared</c> en <c>RescheduleService</c>. Die zijn nog niet vertaald — een aanzienlijk
/// grotere, apart te verifiëren stap. Zie docs/ARCHITECTUUR-DATABASE-TIERS.md §16 en §25.
/// </para>
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

    /// <summary>
    /// Teamrooster: per zaterdag tot het seizoenseinde of het team vrij is, en de wedstrijdenlijst.
    /// Met <c>?format=html</c> een leesbare pagina in plaats van JSON — zelfde twee vormen als op de
    /// SQL Server-tier.
    /// </summary>
    [Function("GetTeamSchedule")]
    public static async Task<IActionResult> GetTeamSchedule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "planner/team-schedule")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("GetTeamSchedule");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            var team = req.Query["team"].ToString();
            if (string.IsNullOrWhiteSpace(team))
                return new BadRequestObjectResult(new { error = "Query parameter 'team' is verplicht." });

            var format = req.Query["format"].ToString().ToLowerInvariant();

            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            log.LogInformation("GetTeamSchedule: team={Team}, format={Format}, club={Club}", team, format, clubCode);

            var schedule = await TeamScheduleService.GetTeamScheduleAsync(
                PostgresDatabaseConfig.ConnectionString, team, PostgresClubScope.Resolve(clubCode));
            if (schedule == null)
                return new NotFoundObjectResult(new { error = $"Team '{team}' niet gevonden." });

            if (format == "html")
            {
                var html = TeamScheduleHtmlRenderer.Render(schedule);
                return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = 200 };
            }

            return new OkObjectResult(schedule);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "GetTeamSchedule failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }
}
