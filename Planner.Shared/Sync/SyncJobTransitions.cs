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

    /// <summary>
    /// De claim (UPDATE ... WHERE status = 'pending' met RETURNING/OUTPUT van de id) is gelukt
    /// precies als de database de id van de bijgewerkte rij teruggeeft; geen rij = al geclaimd.
    /// </summary>
    public static bool IsClaimGranted(object? claimedId) => claimedId is Guid;

    /// <summary>Begrenst een foutmelding tot <see cref="MaxErrorMessageLength"/> tekens; null blijft null.</summary>
    public static string? TruncateError(string? errorMessage)
        => errorMessage is null ? null
            : errorMessage.Length <= MaxErrorMessageLength ? errorMessage
            : errorMessage[..MaxErrorMessageLength];
}
