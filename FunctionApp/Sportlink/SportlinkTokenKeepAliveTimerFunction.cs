using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Integrations.SportlinkClub;

namespace SportlinkFunction.Sportlink;

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

        // Zie SportlinkContractCheckTimerFunction: deze tier laadt de instellingencache via
        // WaitForDatabaseAsync, vóór de toggle gelezen wordt.
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Database niet bereikbaar — keep-alive overgeslagen.");
            return;
        }

        var sportlinkClient = SportlinkEndpointSupport.ClientVoorTimer(context, log, "keep-alive");
        if (sportlinkClient == null) return;

        await SportlinkKeepAliveCore.RunAsync(sportlinkClient, Array.Empty<string>(),
            context.InstanceServices.GetService<ISportlinkAutoLoginStore>(), log);
    }
}
