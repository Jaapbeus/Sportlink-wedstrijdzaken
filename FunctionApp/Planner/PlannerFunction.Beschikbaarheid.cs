using Planner.Endpoints.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Planner.Shared;
using Planner.Shared.Email;

namespace SportlinkFunction.Planner;

public static partial class PlannerFunction
{
    // #1811: begrensde UTF-8/JSON-lezing (413 bij te groot, 400 bij ongeldige JSON), net als de andere
    // planner-endpoints; de gedeelde inhoud uit #1809 blijft ongewijzigd.
    internal static Task<IActionResult> CheckAvailabilityInhoudAsync(
        HttpRequest req, ILogger log, string clubCode,
        Func<CheckAvailabilityRequest, Task<CheckAvailabilityResponse>> controleer)
        => BoundedRequestBodyReader.ExecuteJsonAsync<CheckAvailabilityRequest>(req, async request =>
        {
            if (request == null || string.IsNullOrEmpty(request.Datum))
                return new BadRequestObjectResult(new { error = "Request body met 'datum' veld is verplicht." });

            log.LogInformation("CheckAvailability: datum={Datum}, tijd={Tijd}, team={Team}, cat={Cat}, club={Club}",
                LogWaarde.Schoon(request.Datum), LogWaarde.Schoon(request.AanvangsTijd), LogWaarde.Schoon(request.TeamNaam), LogWaarde.Schoon(request.LeeftijdsCategorie), clubCode);

            var response = await controleer(request);
            AiVeldSanering.SaneerWaarschuwingen(response.Waarschuwingen);

            return new OkObjectResult(response);
        });
}
