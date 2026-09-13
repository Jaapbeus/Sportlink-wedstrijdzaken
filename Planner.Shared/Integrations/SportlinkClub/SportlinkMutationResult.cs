namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Resultaat van een schrijvende Sportlink-aanroep (#992 e.v.). <c>IsSuccess=false</c> met
/// <see cref="SportlinkClubResponse{T}.Status"/><c>=Ok</c> betekent: de aanroep zelf is gelukt,
/// maar Sportlink heeft de mutatie zelf afgewezen (validatiefout, bijv. een niet-toegestane
/// kleedkamercombinatie) — dat is geen <c>SportlinkFout</c> (onze kant/verbinding), maar een
/// inhoudelijke weigering die de aanroeper aan de gebruiker moet tonen.
/// </summary>
/// <param name="IsDryRun">
/// #998: <c>true</c> als de dry-run-modus actief was — de PUT/POST is dan NIET naar Sportlink
/// verstuurd, alleen gelogd. <see cref="IsSuccess"/> is in dat geval altijd <c>true</c>
/// (gesimuleerd succes), <see cref="Violations"/> altijd <c>null</c>.
/// </param>
/// <param name="IsForcedDryRun">
/// #994/#998: <c>true</c> als de PUT/POST werd overgeslagen door de code-niveau
/// <c>forceDryRun</c>-lock (voor een mutatie waarvan de exacte requestbody nog niet live bevestigd
/// is) — ONAFHANKELIJK van de club-instelling <c>sportlinkDryRun</c>. Als dit veld <c>true</c> is,
/// is <see cref="IsDryRun"/> ook altijd <c>true</c>, maar niet omgekeerd: een club kan ook via de
/// gewone instelling dry-run hebben staan zonder dat deze specifieke mutatie code-gelockt is.
/// </param>
/// <param name="PublicMatchId">
/// #997: door Sportlink teruggegeven <c>PublicMatchId</c> van een NIEUW aangemaakte wedstrijd
/// (<c>POST competition/match/clubmatch/ClubMatch</c>) — <c>null</c> voor elke andere mutatie (die
/// werkt altijd op een AL BESTAANDE <c>publicMatchId</c>, die de aanroeper al kent) en ook
/// <c>null</c> zolang <see cref="IsForcedDryRun"/>/<see cref="IsDryRun"/> de aanroep simuleerde.
/// Optioneel/niet-invasief toegevoegd: bestaande aanroepers negeren dit veld gewoon.
/// </param>
public sealed record SportlinkMutationResult(
    bool IsSuccess,
    IReadOnlyList<string>? Violations,
    bool IsDryRun = false,
    bool IsForcedDryRun = false,
    string? PublicMatchId = null);
