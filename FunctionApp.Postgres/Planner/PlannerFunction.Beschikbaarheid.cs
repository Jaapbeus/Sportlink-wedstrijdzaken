using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared;
using Planner.Shared.Email;

namespace FunctionApp.Postgres.Planner;

public static partial class PlannerFunction
{
    internal static async Task<IActionResult> CheckAvailabilityInhoudAsync(
        HttpRequest req, ILogger log, string clubCode,
        Func<CheckAvailabilityRequest, Task<CheckAvailabilityResponse>> controleer)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var request = JsonConvert.DeserializeObject<CheckAvailabilityRequest>(body);
        if (request == null || string.IsNullOrEmpty(request.Datum))
            return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

        log.LogInformation("CheckAvailability: datum={Datum}, tijd={Tijd}, team={Team}, cat={Cat}, club={Club}",
            LogWaarde.Schoon(request.Datum), LogWaarde.Schoon(request.AanvangsTijd), LogWaarde.Schoon(request.TeamNaam), LogWaarde.Schoon(request.LeeftijdsCategorie), clubCode);

        var response = await controleer(request);
        AiVeldSanering.SaneerWaarschuwingen(response.Waarschuwingen);

        return new OkObjectResult(response);
    }
}
