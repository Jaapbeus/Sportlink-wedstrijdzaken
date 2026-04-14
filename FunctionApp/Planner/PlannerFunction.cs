using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SportlinkFunction.Planner
{
    public static class PlannerFunction
    {
        [Function("CheckAvailability")]
        public static async Task<IActionResult> CheckAvailability(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "planner/check-availability")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("CheckAvailability");
            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<CheckAvailabilityRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

                log.LogInformation("CheckAvailability: datum={Datum}, tijd={Tijd}, team={Team}, cat={Cat}",
                    request.Datum, request.AanvangsTijd, request.TeamNaam, request.LeeftijdsCategorie);

                var response = await PlannerService.CheckAvailabilityAsync(request, log);

                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "CheckAvailability failed");
                return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        [Function("DoordeweeksBeschikbaar")]
        public static async Task<IActionResult> DoordeweeksBeschikbaar(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "planner/doordeweeks-beschikbaar")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("DoordeweeksBeschikbaar");
            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<DoordeweeksBeschikbaarRequest>(body)
                    ?? new DoordeweeksBeschikbaarRequest();

                log.LogInformation("DoordeweeksBeschikbaar: dag={Dag}, duur={Duur}, cat={Cat}",
                    request.DagFilter, request.DuurMinuten, request.LeeftijdsCategorie);

                var response = await PlannerService.CheckDoordeweeksBeschikbaarAsync(request, log);

                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "DoordeweeksBeschikbaar failed");
                return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        [Function("BevestigWedstrijd")]
        public static async Task<IActionResult> BevestigWedstrijd(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "planner/bevestig")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("BevestigWedstrijd");
            try
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

                // Bepaal wedstrijdduur
                int duurMinuten = request.WedstrijdDuurMinuten ?? 105;
                decimal veldFractie = 1.00m;
                if (!string.IsNullOrEmpty(request.LeeftijdsCategorie))
                {
                    var speeltijd = await PlannerDataAccess.GetSpeeltijdAsync(request.LeeftijdsCategorie);
                    if (speeltijd != null)
                    {
                        duurMinuten = request.WedstrijdDuurMinuten ?? speeltijd.WedstrijdTotaal;
                        veldFractie = speeltijd.Veldafmeting;
                    }
                }

                var eindTijd = tijd.AddMinutes(duurMinuten);

                var id = await PlannerDataAccess.SavePlannedMatchAsync(
                    date, tijd, eindTijd, request.VeldNummer, veldFractie,
                    request.LeeftijdsCategorie, request.TeamNaam, request.Tegenstander,
                    duurMinuten, request.AangevraagdDoor);

                log.LogInformation("BevestigWedstrijd: saved with id={Id}", id);

                return new OkObjectResult(new
                {
                    id,
                    datum = date.ToString("yyyy-MM-dd"),
                    aanvangsTijd = tijd.ToString("HH:mm"),
                    eindTijd = eindTijd.ToString("HH:mm"),
                    veldNummer = request.VeldNummer,
                    status = "Gepland"
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "BevestigWedstrijd failed");
                return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        [Function("PopulateSunset")]
        public static async Task<IActionResult> PopulateSunset(
            [HttpTrigger(AuthorizationLevel.Admin, "post", Route = "planner/populate-sunset")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("PopulateSunset");
            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                // Vul zonsondergangtabel voor het huidige seizoen
                var today = DateOnly.FromDateTime(DateTime.Today);
                var from = new DateOnly(today.Year, 1, 1);
                var to = new DateOnly(today.Year + 1, 12, 31);

                log.LogInformation("PopulateSunset: computing for {From} to {To}", from, to);
                await PlannerDataAccess.PopulateSunsetTableAsync(from, to);

                return new OkObjectResult(new { message = $"Sunset data populated from {from} to {to}." });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "PopulateSunset failed");
                return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }
        [Function("Optimaliseer")]
        public static async Task<IActionResult> Optimaliseer(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "planner/optimaliseer")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("Optimaliseer");
            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<OptimaliseerRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'datum' is verplicht." });

                log.LogInformation("Optimaliseer: datum={Datum}, doel={Doel}", request.Datum, request.Doel);

                var response = await PlannerService.OptimaliseerAsync(request, log);

                var format = req.Query.ContainsKey("format") ? req.Query["format"].ToString() : "";

                if (response.VoldoendeRuimte && (format == "html" || format == "email"))
                {
                    var meldingHtml = $"<div style='background:#1a3a1a;border:1px solid #2ea043;padding:16px;border-radius:8px;margin-bottom:20px;color:#e6edf3;font-family:sans-serif;'>" +
                        $"<strong>&#10003; {response.VoldoendeRuimteMelding}</strong></div>";
                    var volleHtml = meldingHtml + response.HtmlPlanner;
                    return new ContentResult { Content = volleHtml, ContentType = "text/html", StatusCode = 200 };
                }

                if (format == "html")
                    return new ContentResult { Content = response.HtmlPlanner, ContentType = "text/html", StatusCode = 200 };

                if (format == "email")
                {
                    // Browser-URL opbouwen voor de link in de email
                    var browserUrl = $"{req.Scheme}://{req.Host}/api/planner/optimaliseer?format=html";
                    var emailHtml = PlannerHtmlGenerator.GenereerEmailHtml(
                        DateOnly.Parse(request.Datum),
                        await PlannerDataAccess.GetFieldOccupationsAsync(DateOnly.Parse(request.Datum)),
                        response.Suggesties,
                        await PlannerDataAccess.GetVeldenAsync(),
                        request.Doel ?? "veld5-ontlasten",
                        browserUrl);
                    return new ContentResult { Content = emailHtml, ContentType = "text/html", StatusCode = 200 };
                }

                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Optimaliseer failed");
                return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        [Function("ZoekWedstrijd")]
        public static async Task<IActionResult> ZoekWedstrijd(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "planner/zoek-wedstrijd")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("ZoekWedstrijd");
            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<ZoekWedstrijdRequest>(body);
                if (request == null || string.IsNullOrEmpty(request.TeamNaam) || string.IsNullOrEmpty(request.Datum))
                    return new BadRequestObjectResult(new { error = "Request body met 'teamNaam' en 'datum' is verplicht." });

                if (!DateOnly.TryParse(request.Datum, out var date))
                    return new BadRequestObjectResult(new { error = $"Ongeldige datum: {request.Datum}" });

                log.LogInformation("ZoekWedstrijd: team={Team}, datum={Datum}", request.TeamNaam, request.Datum);

                var match = await PlannerDataAccess.FindMatchAsync(request.TeamNaam, date);
                if (match == null)
                    return new OkObjectResult(new { gevonden = false, reden = $"Geen wedstrijd gevonden voor {request.TeamNaam} op {request.Datum}." });

                return new OkObjectResult(new { gevonden = true, wedstrijd = match });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "ZoekWedstrijd failed");
                return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        [Function("HerplanCheck")]
        public static async Task<IActionResult> HerplanCheck(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "planner/herplan-check")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("HerplanCheck");
            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<HerplanCheckRequest>(body);
                if (request == null || request.Wedstrijdcode == 0)
                    return new BadRequestObjectResult(new { error = "Request body met 'wedstrijdcode' is verplicht." });

                log.LogInformation("HerplanCheck: wedstrijdcode={Code}, voorkeur={Tijd}",
                    request.Wedstrijdcode, request.VoorkeurTijd);

                var response = await PlannerService.CheckRescheduleAvailabilityAsync(request, log);

                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "HerplanCheck failed");
                return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        [Function("Health")]
        public static IActionResult Health(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "health")] HttpRequest req,
            FunctionContext context)
        {
            return new OkObjectResult(new { status = "ok", timestamp = DateTime.UtcNow });
        }

        [Function("HerplanBevestig")]
        public static async Task<IActionResult> HerplanBevestig(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "planner/herplan-bevestig")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("HerplanBevestig");
            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                string body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<HerplanBevestigRequest>(body);
                if (request == null || request.Wedstrijdcode == 0 || string.IsNullOrEmpty(request.GewensteAanvangsTijd))
                    return new BadRequestObjectResult(new { error = "Request body met 'wedstrijdcode' en 'gewensteAanvangsTijd' is verplicht." });

                if (!TimeOnly.TryParse(request.GewensteAanvangsTijd, out var gewensteTijd))
                    return new BadRequestObjectResult(new { error = "Ongeldige tijd." });

                // Haal huidige wedstrijddetails op
                var match = await PlannerDataAccess.FindMatchByCodeAsync(request.Wedstrijdcode);
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
                    request.Opmerking);

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
                return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }
    }
}
