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

                // Resolve duration
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
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "planner/populate-sunset")] HttpRequest req,
            FunctionContext context)
        {
            var log = context.GetLogger("PopulateSunset");
            try
            {
                await SystemUtilities.WaitForDatabaseAsync(log);

                // Populate sunset for current season range based on DateTable
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
    }
}
