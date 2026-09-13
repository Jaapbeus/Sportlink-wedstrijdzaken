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
public sealed record SportlinkMatchChangeValidatie(
    bool ConfirmationNeeded,
    IReadOnlyList<string> ValidationResultMessages,
    bool HasBlockingMessages);

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
