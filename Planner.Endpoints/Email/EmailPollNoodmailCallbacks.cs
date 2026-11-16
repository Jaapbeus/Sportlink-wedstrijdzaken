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
}
