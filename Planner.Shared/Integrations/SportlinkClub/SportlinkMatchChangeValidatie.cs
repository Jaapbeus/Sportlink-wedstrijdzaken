namespace Planner.Shared.Integrations.SportlinkClub;

// NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
// beide PUT's en de bevestigingsvlag vastleggen). Dit bestand hoort uitsluitend bij stap 1
// (valideren) van #995 — er bestaat bewust geen stap 2 (bevestigen): geen endpoint, geen
// client-methode, geen UI-knop daarvoor.

/// <summary>
/// Resultaat van Sportlinks validatiestap voor een datum/tijd/accommodatie-wijzigingsverzoek
/// (#995, epic #986) — geparsed uit het <c>ConfirmationNeeded</c>-veld in de respons van
/// <c>PUT competition/match/UpdateMatchDetails</c>.
/// <para>
/// <b>ONBEVESTIGD:</b> de exacte JSON-vorm is nooit met een netwerktrace gezien (zie issue #995 —
/// de handmatige proef met netwerk-meekijken moest nog gebeuren). <see cref="ValidationResultMessages"/>
/// wordt daarom defensief geparsed: elementen kunnen kale strings zijn óf objecten met een
/// <c>Message</c>- of <c>Description</c>-veld — zie <c>SportlinkClubClient.ParseMatchChangeValidatie</c>.
/// </para>
/// </summary>
/// <param name="ConfirmationNeeded">
/// <c>true</c> als Sportlink een niet-lege <c>ConfirmationNeeded</c>-envelope teruggaf (er is dus
/// een bevestigingsstap nodig — die stap bouwt deze app bewust niet). <c>false</c> als het veld
/// ontbrak of <c>null</c> was.
/// </param>
/// <param name="ValidationResultMessages">Nederlandstalige meldingen van Sportlink, letterlijk door
/// te geven aan de gebruiker — nooit zelf herformuleren (CISO/DPO: geen persoonsgegevens verwacht,
/// maar ook niet transformeren zodat de oorspronkelijke Sportlink-tekst herleidbaar blijft).</param>
/// <param name="HasBlockingMessages">
/// <c>true</c> als minstens één melding blokkerend is — de aanroeper mag dan geen vervolgstap tonen
/// (toch al niet gebouwd in deze app, zie de "NIET VERDER BOUWEN"-marker hierboven).
/// </param>
/// <param name="IsSuccess">
/// Toplevel <c>IsSuccess</c> uit de respons (#1320) — <c>null</c> als het veld ontbrak of geen
/// boolean was. Bij de enige tot nu toe geziene trace (2026-09-26, oefenwedstrijd, dus GEEN bewijs
/// voor de verplichte-wijzigingsverzoek-vorm) stond de eerste respons op <c>false</c> naast een
/// niet-blokkerende melding.
/// </param>
/// <param name="IsMatchChangeRequestMandatory">Toplevel <c>IsMatchChangeRequestMandatory</c> —
/// zelfde onbevestigde status als <see cref="IsSuccess"/>.</param>
/// <param name="IsOwnFacility">Toplevel <c>IsOwnFacility</c> — idem.</param>
/// <param name="IsForceUpdate">
/// Toplevel <c>IsForceUpdate</c> — bij de oefenwedstrijd-trace stond dit veld op <c>true</c> in de
/// TWEEDE (rechtstreeks doorgevoerde) respons. Voor een verplicht wijzigingsverzoek aan een
/// tegenstander is dit nooit waargenomen; behandel deze waarde niet als bevestigd contract.
/// </param>
public sealed record SportlinkMatchChangeValidatie(
    bool ConfirmationNeeded,
    IReadOnlyList<string> ValidationResultMessages,
    bool HasBlockingMessages,
    bool? IsSuccess = null,
    bool? IsMatchChangeRequestMandatory = null,
    bool? IsOwnFacility = null,
    bool? IsForceUpdate = null);

/// <summary>
/// Diagnostiektrace van één #995-stap-1-poging (#1320, eigenaar-gestuurde productieproef) —
/// request-/responsemetadata voor de UI, plus het audit-record-ID zodat een testnotitie eraan
/// gekoppeld kan worden. Bevat bewust geen ruwe request-/response-body: alleen de al
/// gedistilleerde, PII-vrije velden uit <see cref="SportlinkMatchChangeRequestResult"/> — tokens,
/// cookies en autorisatieheaders komen hier nooit in terecht (acceptatiecriterium #1320).
/// </summary>
/// <param name="Resultaat">De mutatie- en validatie-uitkomst, zelfde vorm als vóór #1320.</param>
/// <param name="AuditId"><c>null</c> als er geen audit-service geregistreerd is (lokaal zonder DB).</param>
/// <param name="HttpStatusCode">HTTP-statuscode van Sportlinks respons, of van de transportfout.</param>
/// <param name="Endpoint">Sportlink-pad, bijv. <c>competition/match/UpdateMatchDetails</c>.</param>
/// <param name="HttpMethode">Altijd <c>PUT</c> voor deze mutatie — expliciet meegegeven zodat de UI
/// het niet hoeft aan te nemen.</param>
/// <param name="TijdstipUtc">Serverzijdig tijdstip van deze poging (niet het kloktijdstip van de
/// browser) — UTC, conform §8.1.1 van ARCHITECTUUR.md.</param>
public sealed record SportlinkMatchWijzigingsverzoekTrace(
    SportlinkMatchChangeRequestResult Resultaat,
    long? AuditId,
    int? HttpStatusCode,
    string Endpoint,
    string HttpMethode,
    DateTime TijdstipUtc);

/// <summary>
/// Gecombineerd resultaat van <c>SportlinkClubClient.RequestMatchChangeAsync</c> (#995) — de
/// generieke <see cref="SportlinkMutationResult"/> (transport/dry-run-status, gedeeld met alle
/// Sportlink-mutaties) blijft bewust ongewijzigd/niet vervuild met dit taakspecifieke veld; de
/// validatie-inhoud staat los ernaast in <see cref="Validatie"/>.
/// </summary>
/// <param name="Mutatie">Transport-/dry-run-uitkomst — zelfde semantiek als bij elke andere Sportlink-mutatie.</param>
/// <param name="Validatie">
/// <c>null</c> zolang deze mutatie forceDryRun-gelockt is (geen echte respons om te parsen) — zie
/// <c>SportlinkClubClient.UpdateMatchDetailsChangeRequestLiveBevestigd</c>. Bij een toekomstige,
/// live bevestigde aanroep: het geparste <c>ConfirmationNeeded</c>-resultaat.
/// </param>
public sealed record SportlinkMatchChangeRequestResult(
    SportlinkMutationResult Mutatie,
    SportlinkMatchChangeValidatie? Validatie);
