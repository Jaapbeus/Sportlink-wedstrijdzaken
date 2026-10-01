using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Planner;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Uurlijkse herstelcontrole voor de beveiligde automatische Sportlink-login (#1411).
/// De timer ververs tokenrotatie en start tijdig een nieuwe username/password/TOTP-sessie.
/// </summary>
public static class SportlinkTokenKeepAliveTimerFunction
{
    [Function("SportlinkTokenKeepAlive")]
    public static async Task Run(
        [TimerTrigger("0 0 * * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("SportlinkTokenKeepAlive");

        var sportlinkClient = SportlinkEndpointSupport.ClientVoorTimer(context, log, "keep-alive");
        if (sportlinkClient == null) return;

        await SportlinkKeepAliveCore.RunAsync(sportlinkClient, Array.Empty<string>(),
            context.InstanceServices.GetService<ISportlinkAutoLoginStore>(), log);
    }
}
