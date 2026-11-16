using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>De gedeelde voorwaarden voor één poll, vóór berichten ophalen of mailboxmutaties.</summary>
public static class EmailPollVoorbereiding
{
    public static Task<EmailVerwerkingsInstellingenLeesResultaat?> LeesInstellingenAsync(
        Func<Task<int>>? telOngelezenInboxBerichten,
        Func<Task> wachtOpDatabase,
        Func<bool> instellingenLadenMislukt,
        Func<Task> laadInstellingen,
        Func<Task<EmailVerwerkingsInstellingenLeesResultaat>> leesSnapshot,
        ILogger log,
        Func<Exception, int, Task>? meldDatabaseVerbindingsFout = null,
        Func<Task>? meldDatabaseHerstel = null)
    {
        if (telOngelezenInboxBerichten is null)
        {
            log.LogWarning("Graph-adapter niet beschikbaar; e-mailpoll overgeslagen");
            return Task.FromResult<EmailVerwerkingsInstellingenLeesResultaat?>(null);
        }

        return EmailTimerInstellingenGate.LeesNaInboxProbeAsync(
            telOngelezenInboxBerichten,
            wachtOpDatabase,
            async () =>
            {
                await laadInstellingen();
                if (instellingenLadenMislukt())
                    throw new InvalidOperationException("AppSettings niet beschikbaar");
                return await leesSnapshot();
            },
            () => log.LogWarning("Inbox-metadata niet beschikbaar; e-mailpoll overgeslagen"),
            () => log.LogWarning("E-mailinstellingen niet beschikbaar; verwerking uit"),
            status => log.LogInformation("Email processor uitgeschakeld voor deze club ({Status})", status),
            meldDatabaseVerbindingsFout,
            meldDatabaseHerstel);
    }
}
