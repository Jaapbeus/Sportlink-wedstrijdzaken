using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Shared.Email;

namespace Planner.Endpoints.Email;

/// <summary>Bindt beide pollpoorten aan de bestaande Graph-adapter en persistente noodmail-throttle.</summary>
public static class EmailPollNoodmailCallbacksFactory
{
    public static Task<EmailVerwerkingsInstellingenLeesResultaat?> LeesInstellingenAsync<TThrottle>(
        IEmailPollGraph? graph, IServiceProvider services,
        Func<Task> wachtOpDatabase, Func<bool> instellingenLadenMislukt,
        Func<Task> laadInstellingen,
        Func<Task<EmailVerwerkingsInstellingenLeesResultaat>> leesSnapshot,
        EmailNoodmailTeksten teksten, ILogger log) where TThrottle : INoodmailThrottlePoort
    {
        // Resolutie pas ná de metadata-check en alleen bij verbindingsuitval of herstel.
        var throttle = new Lazy<TThrottle>(() => services.GetRequiredService<TThrottle>());
        return EmailPollVoorbereiding.LeesInstellingenAsync(
            graph is null ? null : graph.TelOngelezenInboxBerichtenAsync,
            wachtOpDatabase, instellingenLadenMislukt, laadInstellingen, leesSnapshot, log,
            (exception, aantal) => EmailNoodmailMelding.BehandelDatabaseVerbindingsFoutAsync(
                exception, graph!, aantal, throttle.Value, teksten, log, "Ongelezen inboxitems (metadata)"),
            () => EmailNoodmailMelding.BehandelDatabaseHerstelAsync(throttle.Value, log));
    }
}
