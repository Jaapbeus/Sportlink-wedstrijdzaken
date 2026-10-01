using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;

namespace SportlinkFunction.Admin;

public static class SportlinkAutoLoginFunction
{
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
