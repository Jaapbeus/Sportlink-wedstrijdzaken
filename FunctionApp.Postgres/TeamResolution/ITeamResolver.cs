namespace FunctionApp.Postgres.TeamResolution;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/TeamResolution/ITeamResolver.cs</c> (#889) —
/// woordelijke kopie. Enige aanspreekpunt voor "ruwe teamnaam-tekst → canonieke TeamId".
/// </summary>
internal interface ITeamResolver
{
    Task<TeamResolutionResult> ResolveAsync(TeamResolutionRequest request);
}
