using Planner.Shared.Integrations.SportlinkClub;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// Service voor audit-logging van Sportlink-mutaties (rollen, veld-toewijzingen, etc.).
/// </summary>
public interface ISportlinkMutationAuditService
{
    /// <summary>
    /// Loggt een poging tot wijziging en geeft het audit-record-ID terug.
    /// </summary>
    Task<long> LogPogingAsync(SportlinkMutationAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Markeert een audit-record als voltooid met resultaat en optionele foutmelding.
    /// </summary>
    Task VoltooiAsync(long auditId, string resultaat, string? foutmeldingSamenvatting, CancellationToken cancellationToken = default);
}
