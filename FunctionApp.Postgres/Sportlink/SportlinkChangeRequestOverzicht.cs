using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Eigen wedstrijdcontext bij een wijzigingsverzoek (#1111, epic #986): wat <c>/wijzigingsverzoeken</c>
/// nodig heeft om een verzoek herkenbaar te maken — wedstrijdnummer, teams, datum, tijd,
/// accommodatie. Komt uit <c>his.matches</c> via de PublicMatchId-cache
/// (<c>public.sportlinkpublicmatchidcache</c>, #991), NIET uit extra velden van Sportlinks
/// <c>MatchChangeRequests</c>-respons: die velden zijn nooit met een live netwerktrace bevestigd
/// (issue #1111, "bekende technische aanname"), en een netwerktrace mag nooit door een agent
/// gemaakt worden (docs/SPORTLINK-WEB-EXTENSION.md §4.4). Onze eigen data is er al en is
/// gegarandeerd juist voor onze eigen wedstrijden.
/// <para>
/// <c>null</c> op het verzoek betekent: geen cache-treffer voor dit <c>PublicMatchId</c> — de
/// wedstrijd is nog niet door de warmup-timer (#1017) of een paneel-lookup geraakt, of het is geen
/// wedstrijd van deze club. Het verzoek blijft dan gewoon zichtbaar, zonder context.
/// </para>
/// </summary>
public sealed record SportlinkWedstrijdContext(
    long Wedstrijdcode,
    long? Wedstrijdnummer,
    string? Thuisteam,
    string? Uitteam,
    string? Datum,
    string? Tijd,
    string? Accommodatie);

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
