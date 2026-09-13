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
    /// Haalt dezelfde <c>Match</c>-respons op als <see cref="GetMatchAsync"/>, maar ongedeserialiseerd
    /// — bedoeld voor <see cref="SportlinkMatchContract"/>'s rauwe vormcontrole (#998, dagelijkse
    /// contract-check-timer). <c>System.Text.Json</c> laat een ontbrekend/hernoemd veld stilzwijgend
    /// op de default vallen; alleen de rauwe JSON-tekst maakt een expliciete "bestaat dit veld nog,
    /// met dit JSON-type" controle mogelijk.
    /// </summary>
    /// <returns>Bij <c>Status=Ok</c>: de rauwe JSON-responstekst. Deze methode logt de inhoud zelf
    /// nooit — dat blijft aan de aanroeper (die alleen veldNAMEN mag loggen, nooit waarden).</returns>
    Task<SportlinkClubResponse<string>> GetMatchRawJsonAsync(
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

    /// <summary>
    /// Haalt alle inkomende wijzigingsverzoeken op (#996, epic #986) —
    /// <c>competition/match/changerequest/MatchChangeRequests</c>. Niet club-/wedstrijd-gescoped —
    /// filter zelf op <c>RequestStatus == "CONFIRM"</c> voor verzoeken die op ons wachten.
    /// </summary>
    Task<SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>> GetChangeRequestsAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Keurt een inkomend wijzigingsverzoek goed of af (#996) —
    /// <c>PUT competition/match/changerequest/MatchChangeRequestAction</c>. Haalt zelf
    /// <c>PublicPersonId</c> van de ingelogde (service-)gebruiker op via <c>user/UserInfo</c> —
    /// de aanroeper hoeft dat niet te weten.
    /// </summary>
    /// <param name="actie"><c>"APPROVE"</c> of <c>"DENY"</c>.</param>
    /// <param name="remarks">Verplicht bij afwijzen (toelichting) — validatie hiervan is aan de aanroeper.</param>
    Task<SportlinkClubResponse<SportlinkMutationResult>> ActOnChangeRequestAsync(
        string functioneleRol,
        string actie,
        string publicMatchId,
        string publicRequestId,
        string? remarks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wijst officials (scheidsrechter, assistenten) toe aan een wedstrijd (#994, epic #986) —
    /// <c>PUT competition/match/official/MatchOfficialsAction</c>.
    /// <b>ONBEVESTIGD:</b> dit endpoint en de body-vorm zijn nooit met een netwerktrace gezien
    /// (gereverse-engineerd uit Sportlinks eigen frontend-code) — deze methode roept daarom altijd
    /// intern <c>forceDryRun: true</c> aan totdat een mens (nooit een agent, zie
    /// docs/SPORTLINK-WEB-EXTENSION.md §4.4) een live trace heeft gedaan en de lock-constante in een
    /// aparte PR omzet. Dit is ONAFHANKELIJK van de club-instelling <c>sportlinkDryRun</c>.
    /// <b>De aanroeper controleert VOORAF</b> <c>SportlinkMutationGuard.MagMuteren(match,
    /// SportlinkMutationSoort.Officials)</c>.
    /// <para>
    /// <b>AVG:</b> roept nooit een Sportlink-zoek-/personendetail-endpoint aan — de aanroeper geeft
    /// uitsluitend een door de beheerder ingevoerde relatiecode/persoons-ID per positie mee, nooit
    /// een naam.
    /// </para>
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup.</param>
    /// <param name="publicMatchId">Zie de TODO(#987)-waarschuwing op <see cref="GetMatchAsync"/>.</param>
    /// <param name="officials">Eén regel per te (her)bezetten positie — zie <see cref="SportlinkOfficialToewijzing"/>.</param>
    /// <returns>
    /// Bij <c>Status=Ok</c>: <c>Data.IsForcedDryRun</c> is in de praktijk altijd <c>true</c> zolang
    /// de code-lock actief is. <c>Data.IsSuccess=false</c> betekent ofwel een transport-afwijzing
    /// (HTTP 420-vorm) ofwel — taakspecifiek voor dit endpoint — dat minstens één official een
    /// <c>ValidationDescription</c> had (Sportlinks "opgeslagen met fouten"); zie
    /// <c>Data.Violations</c> voor de/detail(s).
    /// </returns>
    Task<SportlinkClubResponse<SportlinkMutationResult>> AssignOfficialsAsync(
        string functioneleRol,
        string publicMatchId,
        IReadOnlyList<SportlinkOfficialToewijzing> officials,
        CancellationToken cancellationToken = default);

    // NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
    // beide PUT's en de bevestigingsvlag vastleggen). Dit is uitsluitend stap 1 (valideren) van
    // Sportlinks tweestaps flow — er bestaat bewust geen stap 2 (bevestigen): geen endpoint, geen
    // client-methode, geen UI-knop daarvoor.
    /// <summary>
    /// Vraagt een wijziging van datum/tijd/accommodatie aan (#995, epic #986) — stap 1 (valideren)
    /// van Sportlinks tweestaps flow, via hetzelfde endpoint als #993's veld-wijziging:
    /// <c>PUT competition/match/UpdateMatchDetails</c>. <b>ONBEVESTIGD, altijd code-gelockt:</b> dit
    /// is de enige Sportlink-mutatie die een ECHTE tegenstander raakt (Sportlink stuurt bij
    /// bevestiging een goedkeuringsverzoek naar de tegenstander) — deze methode roept daarom altijd
    /// intern <c>forceDryRun: true</c> aan totdat een mens (nooit een agent, zie
    /// docs/SPORTLINK-WEB-EXTENSION.md §4.4) een live trace heeft gedaan, ONAFHANKELIJK van de
    /// club-instelling <c>sportlinkDryRun</c>. Zelfs stap 1 kan in werkelijkheid al het gevaarlijke
    /// moment zijn als Sportlinks eerste PUT geen "dry validate" blijkt te zijn — de code-lock vangt
    /// dat softwarematig af zolang die aanstaat.
    /// <b>De aanroeper controleert VOORAF</b> <c>SportlinkMutationGuard.MagMuteren(match,
    /// SportlinkMutationSoort.DatumTijdAccommodatie)</c>.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup.</param>
    /// <param name="publicMatchId">Zie de TODO(#987)-waarschuwing op <see cref="GetMatchAsync"/>.</param>
    /// <param name="nieuweDatum">Nieuwe wedstrijddatum, of <c>null</c> om de datum ongewijzigd te laten.</param>
    /// <param name="nieuweStartTijd">Nieuwe starttijd, of <c>null</c> om de tijd ongewijzigd te laten.</param>
    /// <param name="nieuweFacilityId">Nieuwe accommodatie-ID, of <c>null</c> om de accommodatie ongewijzigd te laten.</param>
    /// <param name="toelichting">Verplichte toelichting bij het verzoek — validatie hiervan is aan de aanroeper.</param>
    /// <returns>
    /// Bij <c>Status=Ok</c>: <c>Data.Mutatie.IsForcedDryRun</c> is in de praktijk altijd <c>true</c>
    /// zolang de code-lock actief is, en <c>Data.Validatie</c> dus altijd <c>null</c>.
    /// </returns>
    Task<SportlinkClubResponse<SportlinkMatchChangeRequestResult>> RequestMatchChangeAsync(
        string functioneleRol,
        string publicMatchId,
        DateOnly? nieuweDatum,
        TimeOnly? nieuweStartTijd,
        string? nieuweFacilityId,
        string toelichting,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Maakt een nieuwe oefenwedstrijd ("clubwedstrijd") aan bij Sportlink (#997, epic #986) —
    /// <c>POST competition/match/clubmatch/ClubMatch</c>. Structureel anders dan de andere
    /// mutatiemethodes in deze interface: er is vooraf GEEN bestaande wedstrijd, dus geen
    /// <c>publicMatchId</c> en geen <see cref="SportlinkMutationGuard"/>-check mogelijk (die guard
    /// leest vlaggen van een al bestaande <c>SportlinkMatch</c>). De aanroeper controleert in plaats
    /// daarvan alleen onze EIGEN regels (de <c>sportlinkExtensionEnabled</c>-toggle en
    /// <c>EgressGuard.ExternalIntegrationsAllowed()</c>) — zie <c>SportlinkClubMatchFunction</c>.
    /// <b>ONBEVESTIGD:</b> endpoint en body-vorm zijn nooit met een netwerktrace gezien
    /// (gereverse-engineerd uit Sportlinks eigen frontend-code) — deze methode roept daarom altijd
    /// intern <c>forceDryRun: true</c> aan totdat een mens (nooit een agent, zie
    /// docs/SPORTLINK-WEB-EXTENSION.md §4.4) een live trace heeft gedaan en de lock-constante in een
    /// aparte PR omzet. Dit is ONAFHANKELIJK van de club-instelling <c>sportlinkDryRun</c>.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup.</param>
    /// <param name="aanvraag">Zie <see cref="SportlinkClubMatchAanvraag"/> — elk veld ONBEVESTIGD.</param>
    /// <returns>
    /// Bij <c>Status=Ok</c>: <c>Data.IsForcedDryRun</c> is in de praktijk altijd <c>true</c> zolang
    /// de code-lock actief is, en <c>Data.PublicMatchId</c> blijft dan <c>null</c> (Sportlink is
    /// niet daadwerkelijk aangeroepen). Zodra de lock ooit wordt opgeheven: <c>Data.PublicMatchId</c>
    /// bevat de nieuw aangemaakte wedstrijd-ID uit Sportlinks respons.
    /// </returns>
    Task<SportlinkClubResponse<SportlinkMutationResult>> CreateClubMatchAsync(
        string functioneleRol,
        SportlinkClubMatchAanvraag aanvraag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Haalt de twee ondersteunende picklists op die een oefenwedstrijd-formulier nodig heeft
    /// (#997, bewust beperkte scope): <c>clubmatch/PickListsTeams</c> en
    /// <c>clubmatch/PickListsLocation</c>. Read-only en persoonsgegevensvrij (teams/locaties, geen
    /// personen) — anders dan <see cref="CreateClubMatchAsync"/> dus GEEN forceDryRun-lock nodig,
    /// deze aanroep gaat echt naar Sportlink. Wel ONBEVESTIGD qua exacte respons-veldnamen per item
    /// — zie <see cref="SportlinkPickListItem"/>.
    /// </summary>
    Task<SportlinkClubResponse<SportlinkClubMatchPickLists>> GetClubMatchPickListsAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default);
}
