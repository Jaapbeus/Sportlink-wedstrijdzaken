using FunctionApp.Postgres.Sportlink;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Admin;

/// <summary>Automatische Sportlink-login (#1411): beheer (admin) en de afgeleide koppelingsstatus
/// voor de werkpagina's (#1773, Wedstrijdzaken-poort). Beide lezen de beveiligde opslag via
/// <see cref="ISportlinkAutoLoginStore"/> en geven nooit een secret terug.</summary>
public static class SportlinkAutoLoginFunction
{
    /// <summary>
    /// <c>GET /api/sportlink/koppeling-status</c> (#1773) — koppelingsstatus van rol Wedstrijdzaken
    /// voor de vooraf-melding op "Wedstrijd aanmaken", "Wijzigingsverzoeken", Planning en Veld
    /// optimalisatie. Achter de Wedstrijdzaken-poort (de health-endpoint is admin-only). Leest alleen
    /// de eigen opslag, geen Sportlink-aanroep; orkestratie in <see cref="SportlinkKoppelingStatusEndpointCore"/>.
    /// </summary>
    [Function("SportlinkKoppelingStatusGet")]
    public static Task<IActionResult> GetKoppelingStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/koppeling-status")] HttpRequest req,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SportlinkKoppelingStatusGet"), "koppelingsstatus ophalen",
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
