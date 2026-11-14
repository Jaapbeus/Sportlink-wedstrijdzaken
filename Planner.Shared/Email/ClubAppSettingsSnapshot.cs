namespace Planner.Shared.Email;

/// <summary>
/// Clubinstellingen die een dry-run expliciet meekrijgt. Een ontbrekende waarde voor de gekozen
/// club is null; callers vallen daarmee niet terug op instellingen van een andere club.
/// </summary>
public sealed record ClubAppSettingsSnapshot(
    string? PlannerAfzenderNaam,
    string? CoordinatorNaam,
    string? CoordinatorFunctie,
    string? EmailVoetnoot,
    int? HerplanDeadlineDagen,
    bool? KnvbPdfBijlageIngeschakeld = null,
    string? KnvbStandaardRegio = null);
