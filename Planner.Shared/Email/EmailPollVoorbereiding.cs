using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>De gedeelde voorwaarden voor één poll, vóór berichten ophalen of mailboxmutaties.</summary>
public static class EmailPollVoorbereiding
{
    public static Task<EmailVerwerkingsInstellingenLeesResultaat?> LeesInstellingenAsync(
        Func<Task<bool>>? heeftOngelezenInboxBerichten,
        Func<Task> wachtOpDatabase,
        Func<bool> instellingenLadenMislukt,
        Func<Task<EmailVerwerkingsInstellingenLeesResultaat>> leesSnapshot,
        ILogger log)
    {
        if (heeftOngelezenInboxBerichten is null)
        {
            log.LogWarning("Graph-adapter niet beschikbaar; e-mailpoll overgeslagen");
            return Task.FromResult<EmailVerwerkingsInstellingenLeesResultaat?>(null);
        }

        return EmailTimerInstellingenGate.LeesNaInboxProbeAsync(
            heeftOngelezenInboxBerichten,
            wachtOpDatabase,
            async () =>
            {
                if (instellingenLadenMislukt())
                    throw new InvalidOperationException("AppSettings niet beschikbaar");
                return await leesSnapshot();
            },
            () => log.LogWarning("Inbox-metadata niet beschikbaar; e-mailpoll overgeslagen"),
            () => log.LogWarning("E-mailinstellingen niet beschikbaar; verwerking uit"),
            status => log.LogInformation("Email processor uitgeschakeld voor deze club ({Status})", status));
    }
}
