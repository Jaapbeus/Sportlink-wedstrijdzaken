using FunctionApp.Postgres.Sportlink;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Planner.Shared.Autorisatie;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// <c>GET/PUT /api/beheer/rolfeatureinstellingen</c> (#1390, opvolger van #1341/epic #1338) — de
/// volledige toegangsmatrix: per-club, per-rol instelbare zichtbaarheid van elk Admin GUI-menu-item
/// (<see cref="MenuFeatureKeys"/>) plus de 3 Sportlink-mutatieacties (<see cref="SportlinkRolFeature"/>).
/// Instelbare rollen: <see cref="RolNamen.Alle"/> — <c>admin</c> komt hier bewust niet in voor, want
/// admin heeft altijd alles aan (zie o.a. <c>SportlinkMatchFunction.BepaalRolFeatureToestemmingenAsync</c>)
/// en is niet instelbaar. Dit endpoint zelf blijft uitsluitend voor <c>admin</c> bereikbaar (achter
/// <c>AdminEndpoint.ExecuteAsync</c>) — een niet-adminrol kan zichzelf via deze matrix dus nooit
/// meer rechten geven. SQL Server-tegenhanger: <c>FunctionApp/Admin/AdminRolFeatureInstellingenFunction.cs</c>.
/// </summary>
public static class AdminRolFeatureInstellingenFunction
{
    [Function("AdminRolFeatureInstellingenGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/rolfeatureinstellingen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminRolFeatureInstellingenGet"), "rol-feature-instellingen ophalen",
            async clubCode =>
            {
                var matrix = await RolFeatureInstellingenRepository.GetMatrixAsync(
                    clubCode, PostgresDatabaseConfig.ConnectionString);
                return new OkObjectResult(RolFeatureMatrixCore.NaarResponse(matrix));
            });

    [Function("AdminRolFeatureInstellingenPut")]
    public static Task<IActionResult> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "beheer/rolfeatureinstellingen")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminRolFeatureInstellingenPut"), "rol-feature-instelling wijzigen",
            async clubCode =>
            {
                var (cel, fout) = await RolFeatureMatrixCore.VerwerkZetAsync(
                    await new StreamReader(req.Body).ReadToEndAsync(),
                    (rol, sleutel, aan) => RolFeatureInstellingenRepository.SetAsync(clubCode, rol, sleutel, aan, PostgresDatabaseConfig.ConnectionString));
                return cel is null ? new BadRequestObjectResult(new { error = fout }) : new OkObjectResult(cel);
            });
}
