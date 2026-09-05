namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// DTO voor audit-logging van Sportlink-mutaties.
/// Gedeeld tussen SQL Server en Postgres tiers.
/// </summary>
public sealed record SportlinkMutationAuditEntry(
    string ClubCode,
    string FunctioneleRol,
    string TriggerdDoor,
    string PublicMatchId,
    string Actie,
    string? WaardeVoor,
    string? WaardeNa,
    string? CorrelationId);
