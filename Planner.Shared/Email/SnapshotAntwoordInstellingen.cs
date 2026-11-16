namespace Planner.Shared.Email;

/// <summary>Lazy settings adapter; a selected club snapshot never falls back to another club cache.</summary>
public sealed class SnapshotAntwoordInstellingen(
    ClubAppSettingsSnapshot? club, Func<string, string?> cacheGet,
    string ontbreektInClub, string ontbreektInCache) : IAntwoordInstellingen
{
    public bool EmailReviewModusActief => club?.EmailReviewModusActief ?? true;
    public string? EmailVoetnoot => club != null ? club.EmailVoetnoot : cacheGet("emailVoetnoot");
    public string? PlannerAfzenderNaam => club != null ? club.PlannerAfzenderNaam : cacheGet("plannerAfzenderNaam");
    public string? CoordinatorNaam => club != null ? club.CoordinatorNaam : cacheGet("coordinatorNaam");
    public string? CoordinatorFunctie => club != null ? club.CoordinatorFunctie : cacheGet("coordinatorFunctie");
    public string OntbrekendeAfzenderNaamMelding => club != null ? ontbreektInClub : ontbreektInCache;
}
