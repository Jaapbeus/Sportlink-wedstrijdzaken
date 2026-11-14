namespace Planner.Shared.Sync;

/// <summary>Wat het herstel met een sync-job moet doen (#1839).</summary>
public enum SyncJobRecoveryAction
{
    /// <summary>Niets doen: de job loopt nog of is al afgerond.</summary>
    None,

    /// <summary>Opnieuw aanbieden: terug naar "pending" en een nieuw bericht op de queue.</summary>
    Retry,

    /// <summary>Opgeven: de pogingen zijn op, de job wordt "failed" met een melding.</summary>
    GiveUp,
}

/// <summary>
/// Tier-onafhankelijk, begrensd herstelbeleid voor onderbroken sync-jobs (#1839, eigenaarsbesluit
/// 2026-11-11, optie B): een job die 30 minuten op "running" staat geldt als onderbroken en wordt
/// automatisch hoogstens 2 keer opnieuw geprobeerd (dus 3 pogingen in totaal); daarna "failed" met
/// een melding. Een poging telt vanaf de claim (pending naar running). De tiers houden alleen de
/// SQL en de parameterbinding; de SQL gebruikt <see cref="StaleCutoff"/> en <see cref="MaxAttempts"/>
/// zodat dit beleid de enige bron van de drempels blijft.
/// </summary>
public static class SyncJobRecoveryPolicy
{
    /// <summary>Zo lang mag een job op "running" staan voordat hij als onderbroken geldt.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>Hoeveel keer een onderbroken job automatisch opnieuw wordt geprobeerd.</summary>
    public const int MaxRetries = 2;

    /// <summary>Eerste poging plus <see cref="MaxRetries"/> herhalingen.</summary>
    public const int MaxAttempts = MaxRetries + 1;

    /// <summary>Vaste melding bij uitgeputte pogingen; bevat geen details (de job staat in de GUI).</summary>
    public const string ExhaustedMessage =
        "Sync onderbroken en na 3 pogingen opgegeven; start de synchronisatie handmatig opnieuw.";

    /// <summary>Een rij met <c>StartedAt</c> op of vóór dit moment is onderbroken.</summary>
    public static DateTime StaleCutoff(DateTime utcNow) => utcNow - StaleAfter;

    /// <summary>Precies 30 minuten telt al als onderbroken; 29 minuten 59 seconden niet.</summary>
    public static bool IsInterrupted(DateTime? startedAtUtc, DateTime utcNow)
        => startedAtUtc is { } started && started <= StaleCutoff(utcNow);

    /// <summary>
    /// Beslist per job. <paramref name="attempt"/> is het aantal claims tot nu toe. Bij "pending"
    /// met <paramref name="attempt"/> &gt; 0 is <paramref name="startedAtUtc"/> het moment van de
    /// laatste herplaatsing: raakte het queuebericht kwijt, dan wordt het na dezelfde termijn
    /// opnieuw aangeboden zonder een extra poging te verbruiken (de claim telt de poging).
    /// </summary>
    public static SyncJobRecoveryAction Decide(string status, int attempt, DateTime? startedAtUtc, DateTime utcNow)
    {
        if (!IsInterrupted(startedAtUtc, utcNow))
            return SyncJobRecoveryAction.None;

        return status switch
        {
            "running" => attempt < MaxAttempts ? SyncJobRecoveryAction.Retry : SyncJobRecoveryAction.GiveUp,
            "pending" when attempt > 0 => SyncJobRecoveryAction.Retry,
            _ => SyncJobRecoveryAction.None,
        };
    }
}
