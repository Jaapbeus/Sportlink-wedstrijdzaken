using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Planner.Endpoints.Sportlink;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Sportlink/SportlinkAuditFunction.cs</c>
/// (#1320). <c>PUT /api/sportlink/audit/{id}/notitie</c> koppelt een korte testnotitie aan een
/// bestaande Sportlink-mutatieauditpoging — onderdeel van de eigenaar-gestuurde productieproef met
/// trace van validatie en bevestiging (issue #995/#1320). De validatie en HTTP-vertaling staan in
/// <see cref="SportlinkAuditEndpointCore"/> — dit bestand is alleen nog de DI-/routeplumbing.
/// </summary>
public static class SportlinkAuditFunction
{
    [Function("SqlSportlinkAuditNotitiePut")]
    public static Task<IActionResult> PutNotitie(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sportlink/audit/{id:long}/notitie")] HttpRequest req,
        long id,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkAuditNotitiePut"), "sportlink-audit notitie vastleggen",
            async clubCode =>
            {
                var dto = await SportlinkEndpointSupport.LeesBodyAsync<SportlinkAuditNotitieDto>(req);
                var auditService = context.InstanceServices.GetService<ISportlinkMutationAuditService>();
                if (auditService == null)
                    return new ObjectResult(new { error = "Audit-service niet beschikbaar." }) { StatusCode = 503 };

                return await SportlinkAuditEndpointCore.VerwerkNotitieAsync(
                    id, clubCode, dto?.Notitie, (auditId, club, notitie) => auditService.ZetNotitieAsync(auditId, club, notitie));
            });
}
