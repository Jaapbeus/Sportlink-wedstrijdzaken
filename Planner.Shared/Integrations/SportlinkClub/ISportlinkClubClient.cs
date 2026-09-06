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

    /// <summary>
    /// Zoekt het <c>PublicMatchId</c> van een wedstrijd via de #987/#1016-reverse-lookup
    /// (<c>MatchProgramOverview</c> met een 1-daags bereik, matchend op <c>ExternalMatchId</c>).
    /// `PublicMatchId` kan namelijk niet uit onze eigen `wedstrijdcode` berekend worden (#987).
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup.</param>
    /// <param name="wedstrijdnummer">Onze eigen <c>his.matches.wedstrijdnummer</c> (= Sportlinks ExternalMatchId).</param>
    /// <param name="datum">De wedstrijddatum — het bereik moet smal (1 dag) zijn, anders duurt de
    /// aanroep tientallen seconden (zie onderzoeksrapport §2.2).</param>
    /// <returns>
    /// Response met status en de gevonden entry. <c>IsSuccess=false</c> met <c>Status=Ok</c> en
    /// <c>Data=null</c> betekent: de aanroep zelf slaagde, maar deze wedstrijd stond niet in de
    /// respons voor die datum (nog niet bekend bij Sportlink) — dat is geen fout, de aanroeper
    /// onderscheidt dit expliciet van een échte <c>SportlinkFout</c>/<c>NetwerkFout</c>.
    /// </returns>
    Task<SportlinkClubResponse<SportlinkMatchProgramEntry>> ResolvePublicMatchIdAsync(
        string functioneleRol,
        long wedstrijdnummer,
        DateOnly datum,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Haalt het volledige, niet-club-gescoped Sportlink-wedstrijdprogramma op voor één dag. Voor
    /// een aanroeper met meerdere eigen wedstrijden op dezelfde datum (bijv. een achtergrond-warmup,
    /// #1017) is dit ÉÉN trage (12+ s) aanroep in plaats van <see cref="ResolvePublicMatchIdAsync"/>
    /// per wedstrijd afzonderlijk aan te roepen — die laatste gebruikt deze methode intern en
    /// filtert lokaal op <c>ExternalMatchId</c>.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup.</param>
    /// <param name="datum">Het bereik moet smal (1 dag) blijven, zie <see cref="ResolvePublicMatchIdAsync"/>.</param>
    /// <returns>Bij <c>Status=Ok</c>: de volledige (niet-gefilterde) lijst voor die dag — kan leeg
    /// zijn als er geen wedstrijden zijn.</returns>
    Task<SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>> GetMatchProgramOverviewAsync(
        string functioneleRol,
        DateOnly datum,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ververst proactief het access-/refresh-tokenpaar voor <paramref name="functioneleRol"/>,
    /// zonder een Sportlink-inhoudelijke aanroep te doen. Bedoeld voor een periodieke
    /// keep-alive-timer (zie <c>SportlinkTokenKeepAliveTimerFunction</c>), NIET voor gebruik in een
    /// request-pad — een gewone <see cref="GetMatchAsync"/>/<see cref="ResolvePublicMatchIdAsync"/>
    /// ververst het token al lui wanneer nodig.
    /// <para>
    /// <b>Waarom dit nodig is:</b> Keycloak deactiveert een refresh-token na een periode zonder
    /// gebruik (live vastgesteld: <c>invalid_grant: "Token is not active"</c> ondanks dat de
    /// 6-uurs <c>refresh_expires_in</c> nog niet verstreken was, zie issue #990-comment
    /// 2026-09-05). Zonder periodiek, buiten gebruikersactiviteit om verversen raakt de koppeling
    /// dus onbedoeld inactief na een rustige periode (avond/nacht/weekend).
    /// </para>
    /// </summary>
    /// <returns>De status van de ververspoging — <c>Ok</c> betekent geslaagd en teruggeschreven
    /// via <see cref="ISportlinkClubTokenStore"/>. Geeft nooit een tokenwaarde terug.</returns>
    Task<SportlinkClubCallStatus> VerversTokenAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default);
}
