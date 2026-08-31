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
/// <b>De overige negen planner-endpoints zijn NIET vertaald en geven een expliciete 501</b> in
/// plaats van de stille 404 die de afwezigheid van een route anders zou opleveren — zelfde
/// discipline als <c>AdminTeambegeleidingFunction.Doorsturen</c> en <c>AdminSyncFunction</c>
/// vóór #890. Elke 501-melding noemt de daadwerkelijke, geverifieerde ontbrekende afhankelijkheid
/// (niet een generieke "nog niet gedaan"), verdeeld over drie verschillende soorten gaten:
/// </para>
/// <list type="bullet">
/// <item><b>Twee ontbrekende repositories.</b> <c>CheckAvailability</c>, <c>DoordeweeksBeschikbaar</c>
/// en <c>HerplanCheck</c> hangen (via <c>AvailabilityService</c>/<c>RescheduleService</c>) allemaal
/// af van <c>PlannerAvailabilityRepository</c> en <c>TeamRulesRepository</c> — geen van beide heeft
/// een bestand op deze tier — plus zonsondergangdata (zie volgende punt).</item>
/// <item><b>Twee ontbrekende schematabellen.</b> <c>PopulateSunset</c> schrijft naar
/// <c>dbo.Zonsondergang</c>, <c>HerplanBevestig</c> naar <c>planner.HerplanVerzoeken</c> — beide
/// staan als gemotiveerde uitzondering in <c>scripts/ci/check-postgres-table-coverage.sh</c>, dus
/// dit is geen losse aanname maar dezelfde, al bewaakte lijst.</item>
/// <item><b>Ontbrekende <c>PlannerMatchRepository</c>-methoden.</b> <c>BevestigWedstrijd</c>,
/// <c>ZoekWedstrijd</c> en <c>HerplanBevestig</c> hebben verder alleen
/// <c>SavePlannedMatchAsync</c>/<c>FindMatchAsync</c>/<c>FindMatchByCodeAsync</c> nodig — dat zijn
/// de kleinste van de drie gaten, want <c>planner.geplandewedstrijden</c> en <c>his.matches</c>
/// bestaan al.</item>
/// <item><b>De FieldScheduler-engine.</b> <c>AutoPlan</c> en <c>AutoPlanToepassen</c> hangen af van
/// <c>PlannerShared</c> (538 regels), waarvan nog niet is vastgelegd of hij naar
/// <c>Planner.Shared</c> verhuist of een tweede tier-kopie krijgt — een architectuurbeslissing die
/// buiten een implementatie-PR hoort te vallen (precedent §25), niet iets wat deze klasse zelf kan
/// beslissen.
/// </item>
/// </list>
/// <para>Zie docs/ARCHITECTUUR-DATABASE-TIERS.md §16, §25 en §35.</para>
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

    // ── Onvertaalde endpoints — expliciete 501, geen stille 404 (#888) ────────────────────
    // Zie de klasse-doc-comment hierboven voor de drie soorten gaten. Elke melding noemt de
    // daadwerkelijke ontbrekende afhankelijkheid; geen enkele request-body wordt geparsed, want
    // een stub verwerkt hem toch nooit — zelfde patroon als AdminTeambegeleidingFunction.Doorsturen.

    private const string GeenAvailabilityRepository =
        "PlannerAvailabilityRepository en TeamRulesRepository bestaan nog niet op deze tier " +
        "(issue 888).";

    [Function("CheckAvailability")]
    public static Task<IActionResult> CheckAvailability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/check-availability")] HttpRequest req,
        FunctionContext context)
        => Stub501(req, GeenAvailabilityRepository);

    [Function("DoordeweeksBeschikbaar")]
    public static Task<IActionResult> DoordeweeksBeschikbaar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/doordeweeks-beschikbaar")] HttpRequest req,
        FunctionContext context)
        => Stub501(req, GeenAvailabilityRepository);

    [Function("HerplanCheck")]
    public static Task<IActionResult> HerplanCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/herplan-check")] HttpRequest req,
        FunctionContext context)
        => Stub501(req, GeenAvailabilityRepository + " Hangt bovendien af van PlannerMatchRepository.FindMatchByCodeAsync, die op deze tier ook nog ontbreekt.");

    [Function("BevestigWedstrijd")]
    public static Task<IActionResult> BevestigWedstrijd(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/bevestig")] HttpRequest req,
        FunctionContext context)
        => Stub501(req,
            "PlannerMatchRepository.SavePlannedMatchAsync bestaat nog niet op deze tier (issue 888) " +
            "— planner.geplandewedstrijden zelf bestaat al, alleen de schrijfmethode nog niet.");

    [Function("ZoekWedstrijd")]
    public static Task<IActionResult> ZoekWedstrijd(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/zoek-wedstrijd")] HttpRequest req,
        FunctionContext context)
        => Stub501(req,
            "PlannerMatchRepository.FindMatchAsync bestaat nog niet op deze tier (issue 888) " +
            "— his.matches en planner.geplandewedstrijden zelf bestaan al, alleen de zoekmethode nog niet.");

    [Function("HerplanBevestig")]
    public static Task<IActionResult> HerplanBevestig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/herplan-bevestig")] HttpRequest req,
        FunctionContext context)
        => Stub501(req,
            "planner.HerplanVerzoeken bestaat nog niet op deze tier (issue 888, zie ook " +
            "scripts/ci/check-postgres-table-coverage.sh) — PlannerMatchRepository.FindMatchByCodeAsync " +
            "en SaveHerplanVerzoekAsync ontbreken bovendien.");

    [Function("PopulateSunset")]
    public static Task<IActionResult> PopulateSunset(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/populate-sunset")] HttpRequest req,
        FunctionContext context)
        => Stub501(req,
            "dbo.Zonsondergang heeft geen Postgres-tegenhanger (issue 888, zie ook " +
            "scripts/ci/check-postgres-table-coverage.sh).");

    private const string GeenFieldScheduler =
        "De FieldScheduler-planningsmotor (PlannerShared, 538 regels) is nog niet vertaald — de " +
        "beslissing waar die op deze tier woont (Planner.Shared of een tweede tier-kopie) hoort " +
        "buiten een implementatie-PR te vallen (issue 888, zie docs/ARCHITECTUUR-DATABASE-TIERS.md §25).";

    [Function("AutoPlan")]
    public static Task<IActionResult> AutoPlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/auto-plan")] HttpRequest req,
        FunctionContext context)
        => Stub501(req, GeenFieldScheduler);

    [Function("AutoPlanToepassen")]
    public static Task<IActionResult> AutoPlanToepassen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/auto-plan/toepassen")] HttpRequest req,
        FunctionContext context)
        => Stub501(req, GeenFieldScheduler);

    private static Task<IActionResult> Stub501(HttpRequest req, string reden)
    {
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return Task.FromResult(authResult);

        return Task.FromResult<IActionResult>(new ObjectResult(new { error = reden }) { StatusCode = 501 });
    }
}
