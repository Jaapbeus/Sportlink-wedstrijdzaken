namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Aanvraag om een nieuwe oefenwedstrijd ("clubwedstrijd") aan te maken bij Sportlink (#997, epic
/// #986) — <c>POST competition/match/clubmatch/ClubMatch</c>. ELK veld hier is ONBEVESTIGD
/// (issue #997): de body-vorm komt uit Sportlinks eigen frontend-code, nooit met een netwerktrace
/// gezien. Zie <see cref="ISportlinkClubClient.CreateClubMatchAsync"/> voor de forceDryRun-lock die
/// hierdoor verplicht is.
/// </summary>
/// <param name="MatchDateTime">
/// ONBEVESTIGD: datum en starttijd van de wedstrijd — Sportlink verwacht deze volgens het issue
/// "samengevoegd" als één <c>MatchDate</c>-veld; de exacte stringnotatie (ISO 8601 zonder tijdzone
/// aangenomen) is niet live bevestigd.
/// </param>
/// <param name="Duration">ONBEVESTIGD: duur in minuten — het issue noemt 90 als default.</param>
/// <param name="AgeClassCode">
/// ONBEVESTIGD: leeftijdscategorie-code. <c>codetable/AgeClassList</c> (de picklist die dit zou
/// valideren) is in deze ronde bewust NIET aangesloten (zie PR-beschrijving) — dit veld is dus
/// vrije tekst totdat die picklist een volgende ronde wordt toegevoegd.
/// </param>
/// <param name="Description">ONBEVESTIGD: vrije omschrijving van de oefenwedstrijd.</param>
/// <param name="PublicHomeTeamId">
/// ONBEVESTIGD: team-identifier uit <see cref="ISportlinkClubClient.GetClubMatchPickListsAsync"/>
/// (<c>PickListsTeams</c>).
/// </param>
/// <param name="PublicAwayTeamId">ONBEVESTIGD: tegenstander — vrije tekst of eveneens een team-ID uit de picklist.</param>
/// <param name="FacilityId">
/// ONBEVESTIGD: locatie-identifier uit <see cref="ISportlinkClubClient.GetClubMatchPickListsAsync"/>
/// (<c>PickListsLocation</c>).
/// </param>
/// <param name="FieldId">ONBEVESTIGD: optioneel specifiek veld binnen de locatie.</param>
/// <param name="ExternalMatchId">
/// ONBEVESTIGD: onze eigen wedstrijdnummer, indien deze oefenwedstrijd gekoppeld moet worden aan
/// een reeds lokaal geplande wedstrijd (zie <c>planner.geplandewedstrijden.sportlinkwedstrijdcode</c>
/// — niet aangesloten in deze ronde, zie PR-beschrijving).
/// </param>
public sealed record SportlinkClubMatchAanvraag(
    DateTime MatchDateTime,
    int Duration = 90,
    string? AgeClassCode = null,
    string? Description = null,
    string? PublicHomeTeamId = null,
    string? PublicAwayTeamId = null,
    string? FacilityId = null,
    string? FieldId = null,
    long? ExternalMatchId = null);
