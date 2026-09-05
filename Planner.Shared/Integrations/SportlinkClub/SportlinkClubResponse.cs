namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Status van een Sportlink Club API-aanroep.
/// </summary>
public enum SportlinkClubCallStatus
{
    /// <summary>
    /// Aanroep succesvol (HTTP 200-299).
    /// </summary>
    Ok,

    /// <summary>
    /// De functionele rol is niet gekoppeld aan een refresh token
    /// (geen SportlinkClubRefreshToken__{rol} omgevingsvariabele).
    /// </summary>
    RolNietGekoppeld,

    /// <summary>
    /// Refresh token is ongeldig (400 met invalid_grant). Gebruiker moet opnieuw koppelen.
    /// </summary>
    HerkoppelingVereist,

    /// <summary>
    /// Sportlink-fout (niet-2xx HTTP status, JSON-deserialisatiefout, timeout, etc.).
    /// </summary>
    SportlinkFout,

    /// <summary>
    /// Netwerkfout (HttpRequestException, TaskCanceledException).
    /// </summary>
    NetwerkFout
}

/// <summary>
/// Standaard response-container voor Sportlink Club API-aanroepen.
/// </summary>
/// <typeparam name="T">Type van de response-data.</typeparam>
public sealed record SportlinkClubResponse<T>(
    SportlinkClubCallStatus Status,
    T? Data,
    string? FoutmeldingVoorLog,
    int? HttpStatusCode) where T : class
{
    public bool IsSuccess => Status == SportlinkClubCallStatus.Ok && Data != null;
}
