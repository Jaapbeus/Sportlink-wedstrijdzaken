namespace Planner.Shared.Sync;

/// <summary>Vaste contract voor het privacyveilige sync-logboek (#1912).</summary>
public static class SyncLogPolicy
{
    public const int RetentionDays = 30;

    public const string PhaseTeams = "teams";
    public const string PhaseProgramma = "programma";
    public const string PhaseUitslagen = "uitslagen";
    public const string PhaseMatchdetails = "matchdetails";
    public const string PhasePlannerView = "planner-view";
    public const string PhaseCanonicalisatie = "canonicalisatie";
    public const string PhaseReconciliatie = "reconciliatie";
    public const string PhaseConfiguratie = "configuratie";

    public const string StatusPending = "pending";
    public const string StatusRunning = "running";
    public const string StatusSucceeded = "succeeded";
    public const string StatusPartial = "partial";
    public const string StatusFailed = "failed";
    public const string StatusSkipped = "skipped";

    public const string ReasonOperationFailed = "operation_failed";
    public const string ReasonPartialFailure = "partial_failure";
    public const string ReasonConfigurationMissing = "configuration_missing";
    public const string ReasonEgressBlocked = "egress_blocked";
    public const string ReasonProgrammeTruncated = "programme_truncated";
    public const string ReasonReconciliationSkipped = "reconciliation_skipped";
    public const string ReasonSourceFailed = "source_failed";
    public const string ReasonNotApplicable = "not_applicable";

    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        PhaseTeams, PhaseProgramma, PhaseUitslagen, PhaseMatchdetails,
        PhasePlannerView, PhaseCanonicalisatie, PhaseReconciliatie, PhaseConfiguratie
    };

    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal)
    {
        StatusPending, StatusRunning, StatusSucceeded, StatusPartial, StatusFailed, StatusSkipped
    };

    private static readonly HashSet<string> ReasonCodes = new(StringComparer.Ordinal)
    {
        ReasonOperationFailed, ReasonPartialFailure, ReasonConfigurationMissing, ReasonEgressBlocked,
        ReasonProgrammeTruncated, ReasonReconciliationSkipped, ReasonSourceFailed, ReasonNotApplicable
    };

    public static bool IsPhase(string value) => Phases.Contains(value);
    public static bool IsStatus(string value) => Statuses.Contains(value);
    public static bool IsReasonCode(string? value) => value is null || ReasonCodes.Contains(value);

    public static void Validate(SyncLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.JobId == Guid.Empty)
            throw new ArgumentException("Sync-logboek vereist een job-id.", nameof(entry));
        if (entry.Attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(entry), "Sync-logboek vereist een positieve poging.");
        if (!IsPhase(entry.Phase))
            throw new ArgumentException("Onbekende sync-logfase.", nameof(entry));
        if (!IsStatus(entry.Status))
            throw new ArgumentException("Onbekende sync-logstatus.", nameof(entry));
        if (!IsReasonCode(entry.ReasonCode))
            throw new ArgumentException("Onbekende sync-logreden.", nameof(entry));
        RequireUtc(entry.StartedAt, nameof(entry.StartedAt));
        if (entry.CompletedAt is { } completedAt)
        {
            RequireUtc(completedAt, nameof(entry.CompletedAt));
            if (completedAt < entry.StartedAt)
                throw new ArgumentException("Sync-logvoltooiing valt vóór de start.", nameof(entry));
        }
        ValidateCount(entry.Fetched, nameof(entry.Fetched));
        ValidateCount(entry.Added, nameof(entry.Added));
        ValidateCount(entry.Updated, nameof(entry.Updated));
        ValidateCount(entry.Failed, nameof(entry.Failed));
        ValidateCount(entry.Progress, nameof(entry.Progress));
        ValidateCount(entry.Total, nameof(entry.Total));
        if (entry.Progress is { } progress && entry.Total is { } total && progress > total)
            throw new ArgumentException("Sync-logvoortgang kan niet groter zijn dan het totaal.", nameof(entry));
    }

    public static void RequireUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Sync-logboekdatums moeten UTC zijn.", parameterName);
    }

    private static void ValidateCount(int? value, string parameterName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName, "Sync-logboekaantallen mogen niet negatief zijn.");
    }
}
