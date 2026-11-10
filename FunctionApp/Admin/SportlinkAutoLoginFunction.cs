using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Sportlink;

namespace SportlinkFunction.Admin;

/// <summary>SQL Server-tegenhanger van <c>FunctionApp.Postgres/Admin/SportlinkAutoLoginFunction.cs</c>:
/// automatische Sportlink-login beheren (#1411, admin) en de afgeleide koppelingsstatus voor de
/// werkpagina's (#1773, Wedstrijdzaken-poort). Geeft nooit een secret terug.</summary>
public static class SportlinkAutoLoginFunction
{
    /// <summary>
    /// <c>GET /api/sportlink/koppeling-status</c> (#1773) — koppelingsstatus van rol Wedstrijdzaken
    /// voor de vooraf-melding op de werkpagina's. Achter de Wedstrijdzaken-poort, leest alleen de
    /// eigen opslag, geen Sportlink-aanroep; orkestratie in <see cref="SportlinkKoppelingStatusEndpointCore"/>.
    /// </summary>
    [Function("SqlSportlinkKoppelingStatusGet")]
    public static Task<IActionResult> GetKoppelingStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/koppeling-status")] HttpRequest req,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkKoppelingStatusGet"), "koppelingsstatus ophalen",
            clubCode => SportlinkKoppelingStatusEndpointCore.ExecuteAsync(req, clubCode,
                context.InstanceServices.GetService<ISportlinkAutoLoginStore>()));

    [Function("SportlinkAutoLogin")]
    public static Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "put", "delete",
            Route = "beheer/sportlink-extensie/rollen/{rolNaam}/autologin")] HttpRequest req,
        string rolNaam, FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkAutoLogin"),
            "Sportlink automatische login beheren", clubCode =>
                SportlinkAutoLoginEndpointCore.ExecuteAsync(req, clubCode, rolNaam,
                    context.InstanceServices.GetService<ISportlinkAutoLoginStore>()));
}
