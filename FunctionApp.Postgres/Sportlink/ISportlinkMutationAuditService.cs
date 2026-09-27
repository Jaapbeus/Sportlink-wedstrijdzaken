using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

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

    /// <summary>
    /// Koppelt een korte testnotitie aan een bestaande audit-poging (#1320) — bijv. wat de eigenaar
    /// in Sportlink Club zelf zag na een live-testpoging. Scoped op <paramref name="clubCode"/>: een
    /// audit-rij van een andere club wordt nooit gewijzigd. Geeft <c>false</c> terug als er geen rij
    /// bijgewerkt is (verkeerd ID of verkeerde club) — de aanroeper vertaalt dat naar 404.
    /// </summary>
    Task<bool> ZetNotitieAsync(long auditId, string clubCode, string notitie, CancellationToken cancellationToken = default);
}
