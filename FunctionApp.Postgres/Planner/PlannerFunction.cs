using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Planner.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/PlannerFunction.cs</c> (#888). Vertaald
/// zijn <c>Veldbezetting</c> — de "lichtgewicht wat staat er nu gepland"-weergave (#566) zonder
/// FieldScheduler-berekening —, <c>GetTeamSchedule</c> (het teamrooster), <c>BevestigWedstrijd</c>,
/// <c>ZoekWedstrijd</c>, <c>HerplanBevestig</c> (#888 vervolg), en — sinds §41 — ook
/// <c>CheckAvailability</c>, <c>DoordeweeksBeschikbaar</c>, <c>HerplanCheck</c> en
/// <c>PopulateSunset</c>.
/// <para>
/// <b>Alle elf plannerendpoints zijn vertaald</b> — <c>AutoPlan</c> en <c>AutoPlanToepassen</c>
/// als laatste (§42), waarmee deze tier geen enkel niet-geïmplementeerd endpoint meer heeft.
/// <c>AutoPlanRegels</c> en <c>PlannerHtmlGenerator</c> zijn daarbij naar <c>Planner.Shared</c>
/// verhuisd in plaats van gekopieerd, net als de FieldScheduler-engine bij §38.
/// </para>
/// <para>
/// <b>Autorisatie (#1350):</b> elk endpoint loopt via <see cref="AdminEndpoint.ExecuteAsync"/> —
/// dezelfde poort, databasewacht, clubcode-resolutie en 500-fallback als elk beheerendpoint. Tot
/// #1350 stond hier elf keer een losse <c>EasyAuthHelper.RequireAdmin</c>-aanroep met een eigen
/// try/catch; de SQL Server-tier had daarvoor al een eigen <c>HandleAsync</c>-wrapper, die nu ook
/// weg is. Eén patroon op beide tiers.
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
    // Fallback-duur wanneer geen leeftijdscategorie/speeltijd is opgegeven bij handmatige bevestiging.
    private const int DefaultWedstrijdDuurMinutenZonderCategorie = 105;

    [Function("Veldbezetting")]
    public static Task<IActionResult> Veldbezetting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "planner/veldbezetting")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("Veldbezetting");
        return AdminEndpoint.ExecuteAsync(req, log, "veldbezetting ophalen",
            async clubCode =>
            {
                var datumParam = req.Query["datum"].ToString();
                if (string.IsNullOrWhiteSpace(datumParam) || !DateOnly.TryParse(datumParam, out var datum))
                    return new BadRequestObjectResult(new { error = "Query parameter 'datum' (yyyy-MM-dd) is verplicht." });

                log.LogInformation("Veldbezetting: datum={Datum}, club={Club}", datumParam, clubCode);

                var items = await AutoPlanService.VeldbezettingAsync(
                    PostgresDatabaseConfig.ConnectionString, datum, clubCode);
                return new OkObjectResult(items);
            });
    }

    /// <summary>
    /// Teamrooster: per zaterdag tot het seizoenseinde of het team vrij is, en de wedstrijdenlijst.
    /// Met <c>?format=html</c> een leesbare pagina in plaats van JSON — zelfde twee vormen als op de
    /// SQL Server-tier.
    /// </summary>
    [Function("GetTeamSchedule")]
    public static Task<IActionResult> GetTeamSchedule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "planner/team-schedule")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("GetTeamSchedule");
        return AdminEndpoint.ExecuteAsync(req, log, "teamrooster ophalen",
            async clubCode =>
            {
                var team = req.Query["team"].ToString();
                if (string.IsNullOrWhiteSpace(team))
                    return new BadRequestObjectResult(new { error = "Query parameter 'team' is verplicht." });

                var format = req.Query["format"].ToString().ToLowerInvariant();

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
            });
    }

    /// <summary>
    /// Controleert veldbeschikbaarheid — Postgres-vertaling van het gelijknamige SQL Server-endpoint
    /// (issue 888 vervolg, §41). Zie <c>AvailabilityService</c>'s klasse-doc-comment voor de bewuste
    /// scope-beperking (geen real-time Sportlink-API-pad, uitsluitend de DB-bezetting).
    /// </summary>
    [Function("CheckAvailability")]
    public static Task<IActionResult> CheckAvailability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/check-availability")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("CheckAvailability");
        return AdminEndpoint.ExecuteAsync(req, log, "beschikbaarheid controleren",
            async clubCode =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<CheckAvailabilityRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

                log.LogInformation("CheckAvailability: datum={Datum}, tijd={Tijd}, team={Team}, cat={Cat}, club={Club}",
                    request.Datum, request.AanvangsTijd, request.TeamNaam, request.LeeftijdsCategorie, clubCode);

                var response = await AvailabilityService.CheckAvailabilityAsync(
                    PostgresDatabaseConfig.ConnectionString, request, log, clubCode);

                return new OkObjectResult(response);
            });
    }

    /// <summary>
    /// Doordeweekse beschikbaarheid door het seizoen heen — Postgres-vertaling van het gelijknamige
    /// SQL Server-endpoint (issue 888 vervolg, §41).
    /// </summary>
    [Function("DoordeweeksBeschikbaar")]
    public static Task<IActionResult> DoordeweeksBeschikbaar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/doordeweeks-beschikbaar")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("DoordeweeksBeschikbaar");
        return AdminEndpoint.ExecuteAsync(req, log, "doordeweekse beschikbaarheid controleren",
            async clubCode =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<DoordeweeksBeschikbaarRequest>(body)
                    ?? new DoordeweeksBeschikbaarRequest();

                log.LogInformation("DoordeweeksBeschikbaar: dag={Dag}, duur={Duur}, cat={Cat}, club={Club}",
                    request.DagFilter, request.DuurMinuten, request.LeeftijdsCategorie, clubCode);

                var response = await AvailabilityService.CheckDoordeweeksBeschikbaarAsync(
                    PostgresDatabaseConfig.ConnectionString, request, log, clubCode);

                return new OkObjectResult(response);
            });
    }

    /// <summary>
    /// Controleert herplanmogelijkheden voor een bekende wedstrijd — Postgres-vertaling van het
    /// gelijknamige SQL Server-endpoint (issue 888 vervolg, §41).
    /// </summary>
    [Function("HerplanCheck")]
    public static Task<IActionResult> HerplanCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/herplan-check")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("HerplanCheck");
        return AdminEndpoint.ExecuteAsync(req, log, "herplanmogelijkheden controleren",
            async clubCode =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<HerplanCheckRequest>(body);
                if (request == null || request.Wedstrijdcode == 0)
                    return new BadRequestObjectResult(new { error = "Request body met 'wedstrijdcode' is verplicht." });

                log.LogInformation("HerplanCheck: wedstrijdcode={Code}, voorkeur={Tijd}, club={Club}",
                    request.Wedstrijdcode, request.VoorkeurTijd, clubCode);

                var response = await RescheduleService.CheckRescheduleAvailabilityAsync(
                    PostgresDatabaseConfig.ConnectionString, request, log, clubCode);

                return new OkObjectResult(response);
            });
    }

    /// <summary>
    /// Legt een handmatig ingeplande wedstrijd vast — Postgres-vertaling van het gelijknamige
    /// SQL Server-endpoint (#888 vervolg). Zelfde speeltijd-first, override-tweede logica: de
    /// leeftijdscategorie levert standaardwaarden voor duur/veldfractie, expliciete
    /// requestvelden (<c>WedstrijdDuurMinuten</c>, <c>HeelVeld</c>) overschrijven die.
    /// </summary>
    [Function("BevestigWedstrijd")]
    public static Task<IActionResult> BevestigWedstrijd(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/bevestig")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("BevestigWedstrijd");
        return AdminEndpoint.ExecuteAsync(req, log, "wedstrijd bevestigen",
            async clubCode =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<BevestigRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum) || string.IsNullOrEmpty(request.AanvangsTijd))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum', 'aanvangsTijd' en 'veldNummer' is verplicht." });

                log.LogInformation("BevestigWedstrijd: datum={Datum}, tijd={Tijd}, veld={Veld}",
                    request.Datum, request.AanvangsTijd, request.VeldNummer);

                if (!DateOnly.TryParse(request.Datum, out var date) || !TimeOnly.TryParse(request.AanvangsTijd, out var tijd))
                    return new BadRequestObjectResult(new { error = "Ongeldige datum of tijd." });

                var cc = PostgresClubScope.Resolve(clubCode);

                int duurMinuten = request.WedstrijdDuurMinuten ?? DefaultWedstrijdDuurMinutenZonderCategorie;
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

                // Server-side duur-/grenscontrole (#1134, Codex-review #1107 bevinding 9) — vóór elke
                // DB-toegang: duur 0 (of negatief) gaf voorheen stilzwijgend een lege reservering en
                // HTTP 200.
                var validatieFout = PlannerShared.ValidateBevestigInterval(tijd, duurMinuten);
                if (validatieFout != null)
                    return new BadRequestObjectResult(new { error = validatieFout });

                var eindTijd = tijd.AddMinutes(duurMinuten);

                var (id, conflict) = await PlannerMatchRepository.TryConfirmPlannedMatchAsync(
                    PostgresDatabaseConfig.ConnectionString,
                    date, tijd, eindTijd, request.VeldNummer, veldFractie,
                    request.LeeftijdsCategorie, request.TeamNaam, request.Tegenstander,
                    duurMinuten, request.AangevraagdDoor, clubCode);

                if (conflict != null)
                {
                    log.LogInformation("BevestigWedstrijd: conflict met bestaande bezetting op veld {Veld}", request.VeldNummer);
                    return new ConflictObjectResult(new
                    {
                        error = $"Veld {request.VeldNummer} is op {date:yyyy-MM-dd} tussen {tijd:HH:mm} en {eindTijd:HH:mm} al bezet.",
                        conflicterendeWedstrijd = new
                        {
                            wedstrijd = conflict.Wedstrijd,
                            aanvangsTijd = conflict.AanvangsTijd.ToString("HH:mm"),
                            eindTijd = conflict.EindTijd.ToString("HH:mm"),
                            veldNummer = conflict.VeldNummer,
                            veldDeelGebruik = conflict.VeldDeelGebruik,
                            bron = conflict.Bron
                        }
                    });
                }

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

    /// <summary>
    /// Zoekt een gesynchroniseerde wedstrijd van een team op datum — Postgres-vertaling van het
    /// gelijknamige SQL Server-endpoint (#888 vervolg). Geeft <c>gevonden: false</c> terug (HTTP
    /// 200) bij geen match, net als het origineel — een niet-gevonden wedstrijd is geen serverfout.
    /// </summary>
    [Function("ZoekWedstrijd")]
    public static Task<IActionResult> ZoekWedstrijd(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/zoek-wedstrijd")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("ZoekWedstrijd");
        return AdminEndpoint.ExecuteAsync(req, log, "wedstrijd zoeken",
            async clubCode =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<ZoekWedstrijdRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.TeamNaam) || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'teamNaam' en 'datum' is verplicht." });

                if (!DateOnly.TryParse(request.Datum, out var date))
                    return new BadRequestObjectResult(new { error = $"Ongeldige datum: {request.Datum}" });

                log.LogInformation("ZoekWedstrijd: team={Team}, datum={Datum}, club={Club}",
                    request.TeamNaam, request.Datum, clubCode);

                var match = await PlannerMatchRepository.FindMatchAsync(
                    PostgresDatabaseConfig.ConnectionString, request.TeamNaam, date, clubCode);
                if (match == null)
                    return new OkObjectResult(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {request.TeamNaam} op {request.Datum}." });

                return new OkObjectResult(new { gevonden = true, wedstrijd = match });
            });
    }

    /// <summary>
    /// Legt een herplanverzoek vast voor een bekende (gesynchroniseerde) wedstrijd — Postgres-
    /// vertaling van het gelijknamige SQL Server-endpoint (#888 vervolg).
    /// </summary>
    [Function("HerplanBevestig")]
    public static Task<IActionResult> HerplanBevestig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/herplan-bevestig")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("HerplanBevestig");
        return AdminEndpoint.ExecuteAsync(req, log, "herplanverzoek vastleggen",
            async clubCode =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<HerplanBevestigRequest>(body);
                if (request == null || request.Wedstrijdcode == 0 || string.IsNullOrEmpty(request.GewensteAanvangsTijd))
                    return new BadRequestObjectResult(new { error = "Request body met 'wedstrijdcode' en 'gewensteAanvangsTijd' is verplicht." });

                if (!TimeOnly.TryParse(request.GewensteAanvangsTijd, out var gewensteTijd))
                    return new BadRequestObjectResult(new { error = "Ongeldige tijd." });

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
            });
    }

    /// <summary>
    /// Berekent en bewaart zonsondergangtijden voor het lopende en volgende jaar — Postgres-
    /// vertaling van het gelijknamige SQL Server-endpoint (issue 888 vervolg, §41).
    /// </summary>
    [Function("PopulateSunset")]
    public static Task<IActionResult> PopulateSunset(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/populate-sunset")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("PopulateSunset");
        return AdminEndpoint.ExecuteAsync(req, log, "zonsondergangtabel vullen",
            async _ =>
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var from = new DateOnly(today.Year, 1, 1);
                var to = new DateOnly(today.Year + 1, 12, 31);

                log.LogInformation("PopulateSunset: computing for {From} to {To}", from, to);
                await PlannerSettingsRepository.PopulateSunsetTableAsync(PostgresDatabaseConfig.ConnectionString, from, to);

                return new OkObjectResult(new { message = $"Sunset data populated from {from} to {to}." });
            });
    }

    /// <summary>
    /// De dagplanning-optimalisatie (#666) — Postgres-vertaling van het gelijknamige SQL
    /// Server-endpoint (issue 888 vervolg, §42).
    /// </summary>
    [Function("AutoPlan")]
    public static Task<IActionResult> AutoPlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/auto-plan")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AutoPlan");
        return AdminEndpoint.ExecuteAsync(req, log, "dagplanning berekenen",
            async rawClubCode =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<AutoPlanRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

                var clubCode = PostgresClubScope.Resolve(rawClubCode);
                log.LogInformation("AutoPlan: datum={Datum}, buffer={Buffer}, club={Club}",
                    request.Datum, request.BufferMinuten, clubCode);

                var response = await AutoPlanService.AutoPlanAsync(
                    PostgresDatabaseConfig.ConnectionString, request, clubCode, log);

                return new OkObjectResult(response);
            });
    }

    /// <summary>
    /// Past een AutoPlan-resultaat toe op de demowedstrijden — alleen in testmodus (ALLSTARS).
    /// Postgres-vertaling van het gelijknamige SQL Server-endpoint (issue 888 vervolg, §42).
    /// </summary>
    [Function("AutoPlanToepassen")]
    public static Task<IActionResult> AutoPlanToepassen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "planner/auto-plan/toepassen")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AutoPlanToepassen");
        return AdminEndpoint.ExecuteAsync(req, log, "dagplanning toepassen",
            async rawClubCode =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<AutoPlanToepassenRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

                var clubCode = PostgresClubScope.Resolve(rawClubCode);
                log.LogInformation("AutoPlanToepassen: datum={Datum}, club={Club}", request.Datum, clubCode);

                try
                {
                    var response = await AutoPlanService.AutoPlanToepassenAsync(
                        PostgresDatabaseConfig.ConnectionString, request, clubCode, log);

                    return new OkObjectResult(response);
                }
                catch (InvalidOperationException ex)
                {
                    // "Toepassen is alleen beschikbaar in testmodus (ALLSTARS)" — een bewuste weigering,
                    // geen technische storing: 400, niet 500 (de wrapper zou er 500 van maken).
                    log.LogWarning("AutoPlanToepassen geweigerd: {Reden}", ex.Message);
                    return new BadRequestObjectResult(new { error = ex.Message });
                }
            });
    }
}
