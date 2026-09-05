namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Read-only API-client voor Sportlink Club API.
/// Leest wedstrijddetails op, ondersteunt token-vernieuwing per functionele rol.
/// </summary>
public interface ISportlinkClubClient
{
    /// <summary>
    /// Haalt wedstrijddetails op via Sportlink Club API.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup (bijv. "planner").</param>
    /// <param name="publicMatchId">
    /// Unieke wedstrijd-ID van Sportlink Club.
    /// TODO(#987): Deze waarde moet NOOIT automatisch berekend worden uit wedstrijdcode/wedstrijdnummer.
    /// De hypothese "PublicMatchId = 'M' + wedstrijdcode" is weerlegd tegen productiedata
    /// (zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §0/§2.2).
    /// </param>
    /// <param name="cancellationToken">Annulering token.</param>
    /// <returns>
    /// Response met status, match-data (indien succes), en foutmeldingen (bevat nooit tokens).
    /// </returns>
    Task<SportlinkClubResponse<SportlinkMatch>> GetMatchAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default);
}
