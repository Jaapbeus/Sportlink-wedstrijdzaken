namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Enige aanspreekpunt in de codebase voor "ruwe teamnaam-tekst → canonieke TeamId" (#692).
/// Vervangt op termijn de verspreide regex/LIKE-matching in <c>BerichtPipeline</c> en
/// <c>PlannerMatchRepository</c> — zie #698 (shadow-mode) en #699/#700 (cutover + opruimen).
/// </summary>
public interface ITeamResolver
{
    Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request);
}
