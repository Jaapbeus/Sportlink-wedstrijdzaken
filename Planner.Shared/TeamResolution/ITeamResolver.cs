namespace Planner.Shared.TeamResolution;

/// <summary>
/// Enige aanspreekpunt in de codebase voor "ruwe teamnaam-tekst → canonieke TeamId" (#692).
/// Vervangt de verspreide regex/LIKE-matching in <c>BerichtPipeline</c> en
/// <c>PlannerMatchRepository</c> — zie #698 (shadow-mode) en #699/#700 (cutover + opruimen).
/// Sinds #1716 (slice 2) tier-onafhankelijk: beide databasetiers gebruiken deze ene interface.
/// </summary>
public interface ITeamResolver
{
    Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request);
}
