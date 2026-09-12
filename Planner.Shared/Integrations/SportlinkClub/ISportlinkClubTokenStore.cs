namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Abstractie voor opslag van Sportlink Club refresh tokens per functionele rol.
/// </summary>
public interface ISportlinkClubTokenStore
{
    /// <summary>
    /// Leest het huiconstante refresh token voor een rol.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol (bijv. "planner", "veldmeester").</param>
    /// <returns>Refresh token, of null als niet gekoppeld.</returns>
    string? LeesRefreshToken(string functioneleRol);

    /// <summary>
    /// Schrijft een nieuw (geroteerd) refresh token voor een rol.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol.</param>
    /// <param name="nieuwRefreshToken">Het nieuwe refresh token.</param>
    /// <param name="cancellationToken">Annulering token.</param>
    Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default);
}
