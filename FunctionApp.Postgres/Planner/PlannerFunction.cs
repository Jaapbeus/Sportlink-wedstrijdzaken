using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/PlannerFunction.cs</c> (#888). Vertaald
/// zijn <c>Veldbezetting</c> — de "lichtgewicht wat staat er nu gepland"-weergave (#566) zonder
/// FieldScheduler-berekening —, <c>GetTeamSchedule</c> (het teamrooster), <c>BevestigWedstrijd</c>,
/// <c>ZoekWedstrijd</c>, <c>HerplanBevestig</c> (#888 vervolg), en — sinds §41 — ook
/// <c>CheckAvailability</c>, <c>DoordeweeksBeschikbaar</c>, <c>HerplanCheck</c> en
/// <c>PopulateSunset</c>.
/// <para>
/// <b>Alleen <c>AutoPlan</c> en <c>AutoPlanToepassen</c> geven nog een expliciete 501</b> in plaats
/// van de stille 404 die de afwezigheid van een route anders zou opleveren — zelfde discipline als
/// <c>AdminTeambegeleidingFunction.Doorsturen</c> en <c>AdminSyncFunction</c> vóór #890. Ze hangen
/// af van <c>AutoPlanService</c>/<c>PlannerHtmlGenerator</c> (576 regels), die nog niet bestaan op
/// deze tier — de FieldScheduler-engine zelf woont sinds §38 al in <c>Planner.Shared</c> en is dus
/// géén gat meer, alleen de poort ontbreekt nog.
/// </para>
/// <para>
/// <b><c>CheckAvailability</c>/<c>DoordeweeksBeschikbaar</c>/<c>HerplanCheck</c> gebruiken bewust
/// géén real-time Sportlink-API-pad</b> — zie <c>AvailabilityService</c>'s klasse-doc-comment. Dat
/// is een aparte, forse eenheid werk (HTTP-client, <c>EgressGuard</c>-gate, fixture-test), niet een
/// stilzwijgend overgeslagen detail.
/// </para>
/// <para>Zie docs/ARCHITECTUUR-DATABASE-TIERS.md §16, §25, §35, §40 en §41.</para>
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

    /// <summary>
    /// Controleert veldbeschikbaarheid — Postgres-vertaling van het gelijknamige SQL Server-endpoint
    /// (issue 888 vervolg, §41). Zie <c>AvailabilityService</c>'s klasse-doc-comment voor de bewuste
    /// scope-beperking (geen real-time Sportlink-API-pad, uitsluitend de DB-bezetting).
    /// </summary>
    [Function("CheckAvailability")]
    public static async Task<IActionResult> CheckAvailability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/check-availability")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("CheckAvailability");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<CheckAvailabilityRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Datum))
                return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            log.LogInformation("CheckAvailability: datum={Datum}, tijd={Tijd}, team={Team}, cat={Cat}, club={Club}",
                request.Datum, request.AanvangsTijd, request.TeamNaam, request.LeeftijdsCategorie, clubCode);

            var response = await AvailabilityService.CheckAvailabilityAsync(
                PostgresDatabaseConfig.ConnectionString, request, log, clubCode);

            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "CheckAvailability failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Doordeweekse beschikbaarheid door het seizoen heen — Postgres-vertaling van het gelijknamige
    /// SQL Server-endpoint (issue 888 vervolg, §41).
    /// </summary>
    [Function("DoordeweeksBeschikbaar")]
    public static async Task<IActionResult> DoordeweeksBeschikbaar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/doordeweeks-beschikbaar")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("DoordeweeksBeschikbaar");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<DoordeweeksBeschikbaarRequest>(body)
                ?? new DoordeweeksBeschikbaarRequest();

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            log.LogInformation("DoordeweeksBeschikbaar: dag={Dag}, duur={Duur}, cat={Cat}, club={Club}",
                request.DagFilter, request.DuurMinuten, request.LeeftijdsCategorie, clubCode);

            var response = await AvailabilityService.CheckDoordeweeksBeschikbaarAsync(
                PostgresDatabaseConfig.ConnectionString, request, log, clubCode);

            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DoordeweeksBeschikbaar failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Controleert herplanmogelijkheden voor een bekende wedstrijd — Postgres-vertaling van het
    /// gelijknamige SQL Server-endpoint (issue 888 vervolg, §41).
    /// </summary>
    [Function("HerplanCheck")]
    public static async Task<IActionResult> HerplanCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/herplan-check")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("HerplanCheck");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<HerplanCheckRequest>(body);
            if (request == null || request.Wedstrijdcode == 0)
                return new BadRequestObjectResult(new { error = "Request body met 'wedstrijdcode' is verplicht." });

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            log.LogInformation("HerplanCheck: wedstrijdcode={Code}, voorkeur={Tijd}, club={Club}",
                request.Wedstrijdcode, request.VoorkeurTijd, clubCode);

            var response = await RescheduleService.CheckRescheduleAvailabilityAsync(
                PostgresDatabaseConfig.ConnectionString, request, log, clubCode);

            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "HerplanCheck failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Legt een handmatig ingeplande wedstrijd vast — Postgres-vertaling van het gelijknamige
    /// SQL Server-endpoint (#888 vervolg). Zelfde speeltijd-first, override-tweede logica: de
    /// leeftijdscategorie levert standaardwaarden voor duur/veldfractie, expliciete
    /// requestvelden (<c>WedstrijdDuurMinuten</c>, <c>HeelVeld</c>) overschrijven die.
    /// </summary>
    [Function("BevestigWedstrijd")]
    public static async Task<IActionResult> BevestigWedstrijd(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/bevestig")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("BevestigWedstrijd");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<BevestigRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Datum) || string.IsNullOrEmpty(request.AanvangsTijd))
                return new BadRequestObjectResult(new { error = "Request body met 'datum', 'aanvangsTijd' en 'veldNummer' is verplicht." });

            log.LogInformation("BevestigWedstrijd: datum={Datum}, tijd={Tijd}, veld={Veld}",
                request.Datum, request.AanvangsTijd, request.VeldNummer);

            if (!DateOnly.TryParse(request.Datum, out var date) || !TimeOnly.TryParse(request.AanvangsTijd, out var tijd))
                return new BadRequestObjectResult(new { error = "Ongeldige datum of tijd." });

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            var cc = PostgresClubScope.Resolve(clubCode);

            int duurMinuten = request.WedstrijdDuurMinuten ?? 105;
            decimal veldFractie = 1.00m;
            if (!string.IsNullOrEmpty(request.LeeftijdsCategorie))
            {
                var speeltijden = await PlannerSettingsRepository.GetSpeeltijdenLookupAsync(
                    PostgresDatabaseConfig.ConnectionString, cc);
                if (speeltijden.TryGetValue(request.LeeftijdsCategorie, out var speeltijd))
                {
                    duurMinuten = request.WedstrijdDuurMinuten ?? speeltijd.WedstrijdTotaal;
                    veldFractie = speeltijd.Veldafmeting;
                }
            }
            if (request.HeelVeld == true && veldFractie < 1.00m)
                veldFractie = 1.00m;

            var eindTijd = tijd.AddMinutes(duurMinuten);

            var id = await PlannerMatchRepository.SavePlannedMatchAsync(
                PostgresDatabaseConfig.ConnectionString,
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
        }
        catch (Exception ex)
        {
            log.LogError(ex, "BevestigWedstrijd failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Zoekt een gesynchroniseerde wedstrijd van een team op datum — Postgres-vertaling van het
    /// gelijknamige SQL Server-endpoint (#888 vervolg). Geeft <c>gevonden: false</c> terug (HTTP
    /// 200) bij geen match, net als het origineel — een niet-gevonden wedstrijd is geen serverfout.
    /// </summary>
    [Function("ZoekWedstrijd")]
    public static async Task<IActionResult> ZoekWedstrijd(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/zoek-wedstrijd")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("ZoekWedstrijd");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<ZoekWedstrijdRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.TeamNaam) || string.IsNullOrEmpty(request.Datum))
                return new BadRequestObjectResult(new { error = "Request body met 'teamNaam' en 'datum' is verplicht." });

            if (!DateOnly.TryParse(request.Datum, out var date))
                return new BadRequestObjectResult(new { error = $"Ongeldige datum: {request.Datum}" });

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            log.LogInformation("ZoekWedstrijd: team={Team}, datum={Datum}, club={Club}",
                request.TeamNaam, request.Datum, clubCode);

            var match = await PlannerMatchRepository.FindMatchAsync(
                PostgresDatabaseConfig.ConnectionString, request.TeamNaam, date, clubCode);
            if (match == null)
                return new OkObjectResult(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {request.TeamNaam} op {request.Datum}." });

            return new OkObjectResult(new { gevonden = true, wedstrijd = match });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ZoekWedstrijd failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Legt een herplanverzoek vast voor een bekende (gesynchroniseerde) wedstrijd — Postgres-
    /// vertaling van het gelijknamige SQL Server-endpoint (#888 vervolg).
    /// </summary>
    [Function("HerplanBevestig")]
    public static async Task<IActionResult> HerplanBevestig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/herplan-bevestig")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("HerplanBevestig");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<HerplanBevestigRequest>(body);
            if (request == null || request.Wedstrijdcode == 0 || string.IsNullOrEmpty(request.GewensteAanvangsTijd))
                return new BadRequestObjectResult(new { error = "Request body met 'wedstrijdcode' en 'gewensteAanvangsTijd' is verplicht." });

            if (!TimeOnly.TryParse(request.GewensteAanvangsTijd, out var gewensteTijd))
                return new BadRequestObjectResult(new { error = "Ongeldige tijd." });

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
            var match = await PlannerMatchRepository.FindMatchByCodeAsync(
                PostgresDatabaseConfig.ConnectionString, request.Wedstrijdcode, clubCode);
            if (match == null)
                return new OkObjectResult(new { error = $"Wedstrijd met code {request.Wedstrijdcode} niet gevonden." });

            TimeOnly.TryParse(match.AanvangsTijd, out var huidigeAanvang);

            log.LogInformation("HerplanBevestig: wedstrijdcode={Code}, gewenst={Tijd}",
                request.Wedstrijdcode, request.GewensteAanvangsTijd);

            var id = await PlannerMatchRepository.SaveHerplanVerzoekAsync(
                PostgresDatabaseConfig.ConnectionString,
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
        }
        catch (Exception ex)
        {
            log.LogError(ex, "HerplanBevestig failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Berekent en bewaart zonsondergangtijden voor het lopende en volgende jaar — Postgres-
    /// vertaling van het gelijknamige SQL Server-endpoint (issue 888 vervolg, §41).
    /// </summary>
    [Function("PopulateSunset")]
    public static async Task<IActionResult> PopulateSunset(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/populate-sunset")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("PopulateSunset");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var today = DateOnly.FromDateTime(DateTime.Today);
            var from = new DateOnly(today.Year, 1, 1);
            var to = new DateOnly(today.Year + 1, 12, 31);

            log.LogInformation("PopulateSunset: computing for {From} to {To}", from, to);
            await PlannerSettingsRepository.PopulateSunsetTableAsync(PostgresDatabaseConfig.ConnectionString, from, to);

            return new OkObjectResult(new { message = $"Sunset data populated from {from} to {to}." });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "PopulateSunset failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// De dagplanning-optimalisatie (#666) — Postgres-vertaling van het gelijknamige SQL
    /// Server-endpoint (issue 888 vervolg, §42).
    /// </summary>
    [Function("AutoPlan")]
    public static async Task<IActionResult> AutoPlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/auto-plan")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AutoPlan");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<AutoPlanRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Datum))
                return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

            var clubCode = PostgresClubScope.Resolve(EasyAuthHelper.GetClubCodeFromRequest(req));
            log.LogInformation("AutoPlan: datum={Datum}, buffer={Buffer}, club={Club}",
                request.Datum, request.BufferMinuten, clubCode);

            var response = await AutoPlanService.AutoPlanAsync(
                PostgresDatabaseConfig.ConnectionString, request, clubCode, log);

            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AutoPlan failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Past een AutoPlan-resultaat toe op de demowedstrijden — alleen in testmodus (ALLSTARS).
    /// Postgres-vertaling van het gelijknamige SQL Server-endpoint (issue 888 vervolg, §42).
    /// </summary>
    [Function("AutoPlanToepassen")]
    public static async Task<IActionResult> AutoPlanToepassen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/auto-plan/toepassen")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AutoPlanToepassen");
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<AutoPlanToepassenRequest>(body);
            if (request == null || string.IsNullOrEmpty(request.Datum))
                return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

            var clubCode = PostgresClubScope.Resolve(EasyAuthHelper.GetClubCodeFromRequest(req));
            log.LogInformation("AutoPlanToepassen: datum={Datum}, club={Club}", request.Datum, clubCode);

            var response = await AutoPlanService.AutoPlanToepassenAsync(
                PostgresDatabaseConfig.ConnectionString, request, clubCode, log);

            return new OkObjectResult(response);
        }
        catch (InvalidOperationException ex)
        {
            // "Toepassen is alleen beschikbaar in testmodus (ALLSTARS)" — een bewuste weigering,
            // geen technische storing: 400, niet 500.
            log.LogWarning("AutoPlanToepassen geweigerd: {Reden}", ex.Message);
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AutoPlanToepassen failed");
            return new ObjectResult(new { error = "Verzoek mislukt" }) { StatusCode = 500 };
        }
    }
}
