using Microsoft.Extensions.Logging;
using Planner.Shared.Email;

namespace Planner.Endpoints.Email;

/// <summary>Bindt de gedeelde preflight aan dezelfde Graph-throttle en noodmail als de verwerkingsfasen.</summary>
public sealed record EmailPollNoodmailCallbacks(
    Func<Exception, int, Task> BijVerbindingsfout,
    Func<Task> BijHerstel);

public static class EmailPollNoodmailCallbacksFactory
{
    public static EmailPollNoodmailCallbacks Maak(
        IReplyGraph graph, INoodmailThrottlePoort throttle, EmailNoodmailTeksten teksten,
        ILogger log, string labelAantal = "Onverwerkte emails") => new(
        (exception, aantal) => EmailNoodmailMelding.BehandelDatabaseVerbindingsFoutAsync(
            exception, graph, aantal, throttle, teksten, log, labelAantal),
        () => EmailNoodmailMelding.BehandelDatabaseHerstelAsync(throttle, log));

    /// <summary>Dezelfde pollpoort en noodmailafhandeling voor beide databasetiers.</summary>
    public static Task<EmailVerwerkingsInstellingenLeesResultaat?> LeesInstellingenAsync(
        IReplyGraph? graph, Func<Task<int>>? telOngelezenInboxBerichten,
        Func<INoodmailThrottlePoort> throttle,
        Func<Task> wachtOpDatabase, Func<bool> instellingenLadenMislukt,
        Func<Task> laadInstellingen,
        Func<Task<EmailVerwerkingsInstellingenLeesResultaat>> leesSnapshot,
        EmailNoodmailTeksten teksten, ILogger log)
    {
        var callbacks = graph is null ? null : Maak(graph, throttle(), teksten, log,
            "Ongelezen inboxitems (metadata)");
        return EmailPollVoorbereiding.LeesInstellingenAsync(telOngelezenInboxBerichten,
            wachtOpDatabase, instellingenLadenMislukt, laadInstellingen, leesSnapshot, log,
            callbacks?.BijVerbindingsfout, callbacks?.BijHerstel);
    }
}
