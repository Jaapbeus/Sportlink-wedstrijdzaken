using FunctionApp.Postgres.Sportlink;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Planner.Shared.Autorisatie;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// <c>GET /api/planner/menu-toegang</c> (#1647) — welke menu-sleutels de aanroeper mag zien: voor
/// <c>admin</c> alle, voor andere rollen de unie van de sleutels die hun rol(len) in de toegangsmatrix
/// aan hebben staan ("Rechten per rol"), zonder de groep DEBUG. Open voor elke ingelogde rol, want de
/// matrix zelf is admin-only. Uitsluitend UI-gemak voor het hoofdmenu: elk endpoint autoriseert zelf
/// server-side. Logica in <see cref="MenuToegangCore"/>. SQL Server-tegenhanger:
/// <c>FunctionApp/Admin/MenuToegangFunction.cs</c>.
/// </summary>
public static class MenuToegangFunction
{
    [Function("MenuToegangGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "planner/menu-toegang")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAuthenticatedAsync(req, context.GetLogger("MenuToegangGet"), "menu-toegang ophalen",
            async clubCode =>
            {
                var keys = await MenuToegangCore.BepaalToegestaneKeysAsync(
                    EasyAuthHelper.IsAdmin(req),
                    rol => EasyAuthHelper.IsInRole(req, rol),
                    async () => await RolFeatureInstellingenRepository.GetMatrixAsync(
                        clubCode, PostgresDatabaseConfig.ConnectionString));
                return new OkObjectResult(new { FeatureKeys = keys });
            });
}
