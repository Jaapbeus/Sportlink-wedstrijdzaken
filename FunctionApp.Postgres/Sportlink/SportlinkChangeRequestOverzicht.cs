using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Eén rij van <c>GET /api/sportlink/change-requests</c> sinds #1111: het Sportlink-verzoek
/// (letterlijk, <see cref="SportlinkChangeRequest"/>) plus onze eigen <see cref="Wedstrijd"/>-context.
/// De Sportlink-velden blijven onaangeroerd, zodat het contract van #996 niet verandert — er komt
/// alleen een veld bij.
/// </summary>
public sealed record SportlinkChangeRequestOverzichtItem(
    string PublicMatchId,
    string PublicRequestId,
    string RequestStatus,
    SportlinkChangeRequestData? RequestData,
    string? Reason,
    string? Remarks,
    SportlinkWedstrijdContext? Wedstrijd)
{
    /// <summary>Sportlinks statuscode voor "wacht op een beslissing van ons".</summary>
    public const string StatusOpenstaand = "CONFIRM";

    /// <summary>
    /// Koppelt elk verzoek aan zijn wedstrijdcontext en zet openstaande verzoeken bovenaan. Pure
    /// functie — geen database, geen Sportlink — zodat de beslisregel toetsbaar is zonder
    /// integratieharnas. De volgorde binnen een groep is die van Sportlink zelf (stabiele sortering).
    /// </summary>
    public static List<SportlinkChangeRequestOverzichtItem> Verrijk(
        IEnumerable<SportlinkChangeRequest> verzoeken,
        IReadOnlyDictionary<string, SportlinkWedstrijdContext> contextPerPublicMatchId)
        => verzoeken
            .Select(v => new SportlinkChangeRequestOverzichtItem(
                v.PublicMatchId, v.PublicRequestId, v.RequestStatus, v.RequestData, v.Reason, v.Remarks,
                contextPerPublicMatchId.TryGetValue(v.PublicMatchId, out var ctx) ? ctx : null))
            .OrderBy(v => string.Equals(v.RequestStatus, StatusOpenstaand, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
}
