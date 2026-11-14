namespace Planner.Shared.Sync;

/// <summary>
/// Tier-onafhankelijke regels voor de statusovergangen van een sync-job (#1818): welke status een
/// afronding krijgt, hoe het resultaat van de atomaire pending-to-running-claim wordt gelezen en
/// hoe een foutmelding wordt begrensd. De tiers houden alleen SQL-tekst en parameterbinding.
/// </summary>
public static class SyncJobTransitions
{
    public const int MaxErrorMessageLength = 1000;

    /// <summary>Status die een afgeronde job krijgt; de SQL laat alleen running naar deze status gaan.</summary>
    public static string CompletionStatus(bool succeeded) => succeeded ? "succeeded" : "failed";

    /// <summary>Een afgeronde pipeline houdt een deelresultaat apart van een fatale fout.</summary>
    public static string CompletionStatus(SyncUitkomst outcome) =>
        !outcome.GedeeltelijkMislukt ? "succeeded" :
        outcome.MislukteFases.Contains(SyncUitkomst.FaseConfiguratie) ? "failed" : "partial";

    /// <summary>
    /// Leest het pogingnummer uit het resultaat van de claim (UPDATE ... RETURNING attempt, #1839):
    /// een geheel getal &gt; 0 is een gelukte claim, alles anders (geen rij, DBNull) is niet geclaimd.
    /// </summary>
    public static int? ReadClaimedAttempt(object? claimedAttempt)
        => claimedAttempt is int attempt && attempt > 0 ? attempt : null;

    /// <summary>Begrenst een foutmelding tot <see cref="MaxErrorMessageLength"/> tekens; null blijft null.</summary>
    public static string? TruncateError(string? errorMessage)
        => errorMessage is null ? null
            : errorMessage.Length <= MaxErrorMessageLength ? errorMessage
            : errorMessage[..MaxErrorMessageLength];
}
