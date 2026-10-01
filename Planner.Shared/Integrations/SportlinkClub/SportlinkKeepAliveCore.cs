using Microsoft.Extensions.Logging;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Gedeelde timerorkestratie; queries en egress-gates blijven in de eigen tier.</summary>
public static class SportlinkKeepAliveCore
{
    public static async Task RunAsync(ISportlinkClubClient client, IEnumerable<string> registeredRoles,
        ISportlinkAutoLoginStore? autoStore, ILogger logger, CancellationToken ct = default)
    {
        var roles = new HashSet<string>(registeredRoles, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (autoStore is not null && (await autoStore.ReadAsync("Wedstrijdzaken", ct))?.Credentials is not null)
                roles.Add("Wedstrijdzaken");
        }
        catch (Exception)
        {
            logger.LogWarning("Beveiligde Sportlink-loginopslag niet beschikbaar; controleer de hostconfiguratie.");
            return;
        }
        foreach (var role in roles)
        {
            try
            {
                var status = await client.VerversTokenAsync(role, ct);
                logger.LogInformation("Keep-alive voor rol '{Rol}': {Status}", role, status);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                logger.LogWarning("Sportlink keep-alive niet voltooid voor rol '{Rol}'.", role);
            }
        }
    }
}
