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

    /// <summary>
    /// Wijst kleedkamers toe aan een wedstrijd (#992, epic #986) —
    /// <c>PUT competition/match/UpdateMatchDressingRooms</c>. Live bevestigd endpoint (zie
    /// onderzoeksrapport §2.4: 2× getest tijdens onderzoek, toewijzen en terugzetten).
    /// <b>De aanroeper controleert VOORAF</b> <c>SportlinkMutationGuard.MagMuteren(match,
    /// SportlinkMutationSoort.Kleedkamers)</c> — deze methode doet zelf geen guardrail-check, puur
    /// transport.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup.</param>
    /// <param name="publicMatchId">Zie de TODO(#987)-waarschuwing op <see cref="GetMatchAsync"/>.</param>
    /// <param name="homeDressingRoomId">Kleedkamer-ID voor het thuisteam, of <c>null</c> om leeg te laten.</param>
    /// <param name="awayDressingRoomId">Kleedkamer-ID voor het uitteam, of <c>null</c>.</param>
    /// <param name="officialDressingRoomId">Kleedkamer-ID voor de officials, of <c>null</c>.</param>
    /// <returns>
    /// Bij <c>Status=Ok</c>: <c>Data.IsSuccess</c> geeft aan of Sportlink de mutatie zelf accepteerde
    /// — <c>false</c> betekent een inhoudelijke weigering (bijv. een niet-toegestane
    /// kleedkamercombinatie, zie <c>Data.Violations</c>), geen transportfout.
    /// </returns>
    Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateDressingRoomsAsync(
        string functioneleRol,
        string publicMatchId,
        string? homeDressingRoomId,
        string? awayDressingRoomId,
        string? officialDressingRoomId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wijzigt het veld(deel) van een wedstrijd (#993, epic #986) —
    /// <c>PUT competition/match/UpdateMatchField</c>. Body-vorm uit Sportlinks eigen bundle-code
    /// (niet live gestest bij het schrijven van deze methode) — zie onderzoeksrapport §2.4 en
    /// issue #993. <b>De aanroeper controleert VOORAF</b>
    /// <c>SportlinkMutationGuard.MagMuteren(match, SportlinkMutationSoort.Veld)</c>.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup.</param>
    /// <param name="publicMatchId">Zie de TODO(#987)-waarschuwing op <see cref="GetMatchAsync"/>.</param>
    /// <param name="fieldId">Sportlink-veld-ID, bijv. <c>"&lt;FacilityId&gt;-1"</c> of
    /// <c>"&lt;FacilityId&gt;-OUTDOOR_FIELD-6"</c> — op te halen via de picklist-endpoints, niet
    /// hier te construeren.</param>
    /// <param name="fieldSize">Veldgrootte-code (bijv. <c>"1.0"</c> voor heel veld) — als string,
    /// omdat Sportlinks eigen UI dit soms als string verzendt (issue #993, "onzeker").</param>
    /// <param name="fieldOffset">Veldpositie-offset, of <c>null</c>.</param>
    /// <param name="isForceUpdate">
    /// Semantiek NIET bevestigd (issue #993) — vermoedelijk om over een bezettingsconflict heen te
    /// schrijven. Altijd <c>false</c> gebruiken totdat een mens dit live heeft bevestigd; nooit
    /// standaard <c>true</c> aanbieden in een UI.
    /// </param>
    /// <returns>Zelfde semantiek als <see cref="UpdateDressingRoomsAsync"/>.</returns>
    Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateFieldAsync(
        string functioneleRol,
        string publicMatchId,
        string? fieldId,
        string? fieldSize,
        int? fieldOffset,
        bool isForceUpdate,
        CancellationToken cancellationToken = default);
}
