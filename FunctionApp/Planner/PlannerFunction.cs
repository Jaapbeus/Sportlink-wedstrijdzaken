using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SportlinkFunction.Admin;

namespace SportlinkFunction.Planner
{
    public static class PlannerFunction
    {
        private const int DefaultWedstrijdDuurMinuten = 105;
        private const decimal VolledigVeldFractie = 1.00m;

        private static async Task<IActionResult> HandleAsync(HttpRequest req, ILogger log, string operationName, Func<Task<IActionResult>> handler)
        {
            var authResult = EasyAuthHelper.RequireAdmin(req);
            if (authResult != null) return authResult;
            try
            {
                return await handler();
            }
            catch (Exception ex)
            {
                log.LogError(ex, operationName + " failed");
                return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
            }
        }

        private static string RequireClubCode(HttpRequest req) =>
            EasyAuthHelper.GetClubCodeFromRequest(req)
                ?? throw new InvalidOperationException("ClubCode kon niet worden bepaald uit de request — controleer Easy Auth configuratie.");

        [Function("CheckAvailability")]
        public static async Task<IActionResult> CheckAvailability(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/check-availability")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("CheckAvailability");
            return await HandleAsync(req, log, "CheckAvailability", async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<CheckAvailabilityRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

                var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
                log.LogInformation("CheckAvailability: datum={Datum}, tijd={Tijd}, team={Team}, cat={Cat}, club={Club}",
                    request.Datum, request.AanvangsTijd, request.TeamNaam, request.LeeftijdsCategorie, clubCode);

                var response = await PlannerService.CheckAvailabilityAsync(request, log, clubCode);

                return new OkObjectResult(response);
            });
        }

        [Function("DoordeweeksBeschikbaar")]
        public static async Task<IActionResult> DoordeweeksBeschikbaar(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/doordeweeks-beschikbaar")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("DoordeweeksBeschikbaar");
            return await HandleAsync(req, log, "DoordeweeksBeschikbaar", async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<DoordeweeksBeschikbaarRequest>(body)
                    ?? new DoordeweeksBeschikbaarRequest();

                var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
                log.LogInformation("DoordeweeksBeschikbaar: dag={Dag}, duur={Duur}, cat={Cat}, club={Club}",
                    request.DagFilter, request.DuurMinuten, request.LeeftijdsCategorie, clubCode);

                var response = await PlannerService.CheckDoordeweeksBeschikbaarAsync(request, log, clubCode);

                return new OkObjectResult(response);
            });
        }

        [Function("BevestigWedstrijd")]
        public static async Task<IActionResult> BevestigWedstrijd(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/bevestig")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("BevestigWedstrijd");
            return await HandleAsync(req, log, "BevestigWedstrijd", async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<BevestigRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum) || string.IsNullOrEmpty(request.AanvangsTijd))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum', 'aanvangsTijd' en 'veldNummer' is verplicht." });

                log.LogInformation("BevestigWedstrijd: datum={Datum}, tijd={Tijd}, veld={Veld}",
                    request.Datum, request.AanvangsTijd, request.VeldNummer);

                if (!DateOnly.TryParse(request.Datum, out var date) || !TimeOnly.TryParse(request.AanvangsTijd, out var tijd))
                    return new BadRequestObjectResult(new { error = "Ongeldige datum of tijd." });

                int duurMinuten = request.WedstrijdDuurMinuten ?? DefaultWedstrijdDuurMinuten;
                decimal veldFractie = VolledigVeldFractie;
                var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
                if (!string.IsNullOrEmpty(request.LeeftijdsCategorie))
                {
                    var speeltijd = await PlannerDataAccess.GetSpeeltijdAsync(request.LeeftijdsCategorie, clubCode);
                    if (speeltijd != null)
                    {
                        duurMinuten = request.WedstrijdDuurMinuten ?? speeltijd.WedstrijdTotaal;
                        veldFractie = speeltijd.Veldafmeting;
                    }
                }
                // Heel-veld override: als expliciet gevraagd, overschrijf de speeltijd-veldafmeting
                if (request.HeelVeld == true && veldFractie < VolledigVeldFractie)
                    veldFractie = VolledigVeldFractie;

                var eindTijd = tijd.AddMinutes(duurMinuten);

                var id = await PlannerDataAccess.SavePlannedMatchAsync(
                    date, tijd, eindTijd, request.VeldNummer, veldFractie,
                    request.LeeftijdsCategorie, request.TeamNaam, request.Tegenstander,
                    duurMinuten, request.AangevraagdDoor, clubCode);

                log.LogInformation("BevestigWedstrijd: saved with id={Id}", id);

                return new OkObjectResult(new
                {
                    id,
                    datum = date.ToString("yyyy-MM-dd"),
                    aanvangsTijd = tijd.ToString("HH:mm"),
                    eindTijd = eindTijd.ToString("HH:mm"),
                    veldNummer = request.VeldNummer,
                    status = "Te bevestigen"
                });
            });
        }

        [Function("PopulateSunset")]
        public static async Task<IActionResult> PopulateSunset(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/populate-sunset")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("PopulateSunset");
            return await HandleAsync(req, log, "PopulateSunset", async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                var today = DateOnly.FromDateTime(DateTime.Today);
                var from = new DateOnly(today.Year, 1, 1);
                var to = new DateOnly(today.Year + 1, 12, 31);

                log.LogInformation("PopulateSunset: computing for {From} to {To}", from, to);
                await PlannerDataAccess.PopulateSunsetTableAsync(from, to);

                return new OkObjectResult(new { message = $"Sunset data populated from {from} to {to}." });
            });
        }
        // POST /api/planner/optimaliseer is vervallen bij #666. Er is nu één dagplanning-optimalisatie:
        // POST /api/planner/auto-plan (AutoPlanService), die regels, voorkeurstijden en de defaults per
        // leeftijdscategorie in die rangorde toepast. Het oude endpoint negeerde voorkeuren en
        // prioriteiten volledig, waardoor twee knoppen in de GUI verschillende planningen opleverden.
        // De HTML-weergaven zitten in de auto-plan-response (HuidigeHtml / OptimaleHtml).

        [Function("ZoekWedstrijd")]
        public static async Task<IActionResult> ZoekWedstrijd(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/zoek-wedstrijd")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("ZoekWedstrijd");
            return await HandleAsync(req, log, "ZoekWedstrijd", async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<ZoekWedstrijdRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.TeamNaam) || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'teamNaam' en 'datum' is verplicht." });

                if (!DateOnly.TryParse(request.Datum, out var date))
                    return new BadRequestObjectResult(new { error = $"Ongeldige datum: {request.Datum}" });

                var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
                log.LogInformation("ZoekWedstrijd: team={Team}, datum={Datum}, club={Club}",
                    request.TeamNaam, request.Datum, clubCode);

                var match = await PlannerDataAccess.FindMatchAsync(request.TeamNaam, date, clubCode);
                if (match == null)
                    return new OkObjectResult(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {request.TeamNaam} op {request.Datum}." });

                return new OkObjectResult(new { gevonden = true, wedstrijd = match });
            });
        }

        [Function("HerplanCheck")]
        public static async Task<IActionResult> HerplanCheck(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/herplan-check")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("HerplanCheck");
            return await HandleAsync(req, log, "HerplanCheck", async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<HerplanCheckRequest>(body);
                if (request == null || request.Wedstrijdcode == 0)
                    return new BadRequestObjectResult(new { error = "Request body met 'wedstrijdcode' is verplicht." });

                var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
                log.LogInformation("HerplanCheck: wedstrijdcode={Code}, voorkeur={Tijd}, club={Club}",
                    request.Wedstrijdcode, request.VoorkeurTijd, clubCode);

                var response = await PlannerService.CheckRescheduleAvailabilityAsync(request, log, clubCode);

                return new OkObjectResult(response);
            });
        }

        [Function("Health")]
        public static async Task<IActionResult> Health(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req,
            FunctionContext context)
        {
            var version = typeof(PlannerFunction).Assembly.GetName().Version?.ToString(4) ?? "?";
            var (dbStatus, serverVersion) = await GetDatabaseStatusAsync();
            return new OkObjectResult(new
            {
                status = dbStatus == "online" ? "ok" : "degraded",
                version,
                timestamp = DateTime.UtcNow,
                database = dbStatus,
                // #863: tier/provider komen uit build-time assembly-metadata (nooit een runtime-gok),
                // dus altijd gevuld — ook als de database onbereikbaar is. serverVersion komt
                // aantoonbaar uit de database zelf en is daarom null zolang die niet bereikbaar is.
                tier = GetAssemblyMetadata("DatabaseTier") ?? "onbekend",
                provider = GetAssemblyMetadata("DatabaseProvider") ?? "onbekend",
                serverVersion
            });
        }

        // internal zodat FunctionApp.Tests dit rechtstreeks kan afdekken (InternalsVisibleTo, #476)
        // zonder Health() zelf te hoeven aanroepen — dat vereist een HttpRequest/FunctionContext die
        // deze codebase bewust niet namaakt (zie Function1.cs-tests: de logica wordt getest, niet de
        // Azure Functions-trigger-wrapper).
        internal static string? GetAssemblyMetadata(string key) =>
            typeof(PlannerFunction).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value;

        // Geeft ("online"|"paused"|"timeout"|"unavailable"|"unconfigured", serverVersion) terug.
        // serverVersion is alleen gevuld bij "online" — #863 eist dat dit veld aantoonbaar uit de
        // database komt, dus geen fallback-waarde als de verbinding niet lukt.
        // Error 40613 = Azure SQL serverless auto-paused; verbinding triggert automatisch resume.
        private static async Task<(string status, string? serverVersion)> GetDatabaseStatusAsync()
        {
            string connStr;
            try { connStr = SystemUtilities.DatabaseConfig.ConnectionString; }
            catch { return ("unconfigured", null); }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                using var conn = new SqlConnection(connStr);
                await conn.OpenAsync(cts.Token);
                using var cmd = new SqlCommand(
                    "SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128))", conn) { CommandTimeout = 5 };
                var serverVersion = (string?)await cmd.ExecuteScalarAsync(cts.Token);
                return ("online", serverVersion);
            }
            catch (SqlException ex) when (ex.Number == 40613)
            {
                // Database is paused (free tier limiet of normale auto-pause).
                // Azure begint automatisch te resumeren zodra we verbinding proberen.
                return ("paused", null);
            }
            catch (OperationCanceledException)
            {
                return ("timeout", null);
            }
            catch
            {
                return ("unavailable", null);
            }
        }

        [Function("HerplanBevestig")]
        public static async Task<IActionResult> HerplanBevestig(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/herplan-bevestig")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("HerplanBevestig");
            return await HandleAsync(req, log, "HerplanBevestig", async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<HerplanBevestigRequest>(body);
                if (request == null || request.Wedstrijdcode == 0 || string.IsNullOrEmpty(request.GewensteAanvangsTijd))
                    return new BadRequestObjectResult(new { error = "Request body met 'wedstrijdcode' en 'gewensteAanvangsTijd' is verplicht." });

                if (!TimeOnly.TryParse(request.GewensteAanvangsTijd, out var gewensteTijd))
                    return new BadRequestObjectResult(new { error = "Ongeldige tijd." });

                var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
                var match = await PlannerDataAccess.FindMatchByCodeAsync(request.Wedstrijdcode, clubCode);
                if (match == null)
                    return new OkObjectResult(new { error = $"Wedstrijd met code {request.Wedstrijdcode} niet gevonden." });

                TimeOnly.TryParse(match.AanvangsTijd, out var huidigeAanvang);

                log.LogInformation("HerplanBevestig: wedstrijdcode={Code}, gewenst={Tijd}",
                    request.Wedstrijdcode, request.GewensteAanvangsTijd);

                var id = await PlannerDataAccess.SaveHerplanVerzoekAsync(
                    request.Wedstrijdcode,
                    match.Wedstrijd,
                    DateOnly.Parse(match.Datum),
                    huidigeAanvang,
                    match.VeldNaam,
                    gewensteTijd,
                    request.GewenstVeldNummer,
                    request.AangevraagdDoor,
                    request.Opmerking,
                    clubCode);

                log.LogInformation("HerplanBevestig: saved with id={Id}", id);

                return new OkObjectResult(new HerplanBevestigResponse
                {
                    Id = id,
                    Wedstrijdcode = request.Wedstrijdcode,
                    HuidigeWedstrijd = match.Wedstrijd,
                    GewensteAanvangsTijd = request.GewensteAanvangsTijd,
                    GewenstVeldNummer = request.GewenstVeldNummer,
                    Status = "Aangevraagd"
                });
            });
        }

        // ── Auto-plan endpoints (#380) ──

        [Function("AutoPlan")]
        public static async Task<IActionResult> AutoPlan(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/auto-plan")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("AutoPlan");
            return await HandleAsync(req, log, "AutoPlan", async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<AutoPlanRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

                var clubCode = RequireClubCode(req);
                log.LogInformation("AutoPlan: datum={Datum}, club={Club}", request.Datum, clubCode);

                var response = await PlannerService.AutoPlanAsync(request, clubCode, log);
                return new OkObjectResult(response);
            });
        }

        [Function("AutoPlanToepassen")]
        public static async Task<IActionResult> AutoPlanToepassen(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/auto-plan/toepassen")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("AutoPlanToepassen");
            return await HandleAsync(req, log, "AutoPlanToepassen", async () =>
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<AutoPlanToepassenRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

                var clubCode = RequireClubCode(req);

                if (!clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase))
                    return new ObjectResult(new { error = "Toepassen is alleen beschikbaar in testmodus (ALLSTARS)." }) { StatusCode = 403 };

                log.LogInformation("AutoPlanToepassen: datum={Datum}, club={Club}", request.Datum, clubCode);

                var response = await PlannerService.AutoPlanToepassenAsync(request, clubCode, log);
                return new OkObjectResult(response);
            });
        }

        // Lichtgewicht "wat staat er nu gepland"-weergave (#566) — zonder FieldScheduler-berekening.
        [Function("Veldbezetting")]
        public static async Task<IActionResult> Veldbezetting(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "planner/veldbezetting")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("Veldbezetting");
            return await HandleAsync(req, log, "Veldbezetting", async () =>
            {
                var datumParam = req.Query["datum"].ToString();
                if (string.IsNullOrWhiteSpace(datumParam) || !DateOnly.TryParse(datumParam, out var datum))
                    return new BadRequestObjectResult(new { error = "Query parameter 'datum' (yyyy-MM-dd) is verplicht." });

                await SystemUtilities.WaitForDatabaseAsync(log);

                var clubCode = RequireClubCode(req);
                log.LogInformation("Veldbezetting: datum={Datum}, club={Club}", datumParam, clubCode);

                var items = await PlannerService.VeldbezettingAsync(datum, clubCode);
                return new OkObjectResult(items);
            });
        }

        [Function("GetTeamSchedule")]
        public static async Task<IActionResult> GetTeamSchedule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "planner/team-schedule")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("GetTeamSchedule");
            return await HandleAsync(req, log, "GetTeamSchedule", async () =>
            {
                var team = req.Query["team"].ToString();
                if (string.IsNullOrWhiteSpace(team))
                    return new BadRequestObjectResult(new { error = "Query parameter 'team' is verplicht." });

                var format = req.Query["format"].ToString().ToLowerInvariant();

                await SystemUtilities.WaitForDatabaseAsync(log);

                var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
                log.LogInformation("GetTeamSchedule: team={Team}, format={Format}, club={Club}", team, format, clubCode);

                var schedule = await PlannerService.GetTeamScheduleAsync(team, clubCode);
                if (schedule == null)
                    return new NotFoundObjectResult(new { error = $"Team '{team}' niet gevonden." });

                if (format == "html")
                {
                    var html = TeamScheduleHtmlRenderer.Render(schedule);
                    return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = 200 };
                }

                return new OkObjectResult(schedule);
            });
        }
    }
}
