namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Aanvraag om een nieuwe oefenwedstrijd ("clubwedstrijd") aan te maken bij Sportlink —
/// <c>POST competition/match/clubmatch/ClubMatch</c>. <b>Sinds #1427 live bevestigd</b>: elk veld
/// hieronder komt uit een console-trace van Sportlink Clubs eigen formulier (01-10-2026), dat met
/// deze body HTTP 200 en een nieuw <c>PublicMatchId</c> kreeg. Vóór #1427 was de body
/// gereverse-engineerd en gaf hij HTTP 602. <b>Correctie #1871:</b> die trace had aan beide kanten
/// een clubteam; een externe tegenstander gaat met <see cref="GeenTeamId"/> (live bevestigd
/// 2026-11-11 met Sportlinks eigen formulier). Wordt opgebouwd door
/// <see cref="ClubMatchAanvraagBouwer"/>; zie <see cref="SportlinkMutatieBodies.BuildClubMatchBody"/>
/// voor de exacte JSON-notatie.
/// </summary>
/// <param name="MatchDate">Speeldatum; gaat als <c>yyyy-MM-dd</c>.</param>
/// <param name="StartTime">Aanvangstijd; gaat apart als <c>HH:mm:ss</c>.</param>
/// <param name="Duration">Duur in minuten.</param>
/// <param name="ExternalMatchId">Wedstrijdnummer — verplicht in Sportlinks formulier; komt uit
/// <c>ClubMatchDefaults</c> (het eerstvolgende vrije nummer).</param>
/// <param name="Description">Omschrijving — verplicht in Sportlinks formulier.</param>
/// <param name="HomeTeam">Weergavenaam thuisteam (vrije tekst).</param>
/// <param name="AwayTeam">Weergavenaam uitteam (vrije tekst, de tegenstander).</param>
/// <param name="PublicTeamId">Het <c>T…</c>-ID van het eigen (thuis)team; gaat als <c>PublicHomeTeamId</c>.
/// Bij een vrije thuisteamnaam: Sportlinks standaard (live de sentinel <see cref="GeenTeamId"/>).</param>
/// <param name="AgeClassCode">Code uit <c>PickListsMatchInformation.AgeClasses</c>, bijv. "001".</param>
/// <param name="SportIdTag">Uit <c>PickListsMatchInformation.Activities</c>, bijv. "SOCCER-VE-AL/FRIDAY".</param>
/// <param name="IsHomeMatch">Thuiswedstrijd (eigen accommodatie).</param>
/// <param name="FacilityId">Accommodatie uit <c>PickListsLocation</c>.</param>
/// <param name="SubFacilityId">Veld uit <c>PickListsLocation.Facilities[].Fields</c>.</param>
/// <param name="FieldSize">"1.0" = heel veld.</param>
/// <param name="FieldOffset">"0" bij een heel veld.</param>
/// <param name="PublicAwayTeamId">Het team-ID van de tegenstander; gaat als <c>PublicAwayTeamId</c>. Een
/// externe tegenstander (vrije tekst in <paramref name="AwayTeam"/>) krijgt de sentinel <see cref="GeenTeamId"/>,
/// <b>nooit</b> het eigen team-ID (#1871): Sportlink toont de teamnaam bij het ID en negeert dan de tekst,
/// en de koppeling is daarna in Sportlink Club niet meer naar vrije tekst te wijzigen.</param>
public sealed record SportlinkClubMatchAanvraag(
    DateOnly MatchDate,
    TimeOnly StartTime,
    int Duration,
    long? ExternalMatchId,
    string Description,
    string HomeTeam,
    string AwayTeam,
    string? PublicTeamId,
    string? AgeClassCode,
    string? SportIdTag,
    bool IsHomeMatch,
    string? FacilityId,
    string? SubFacilityId,
    string FieldSize = "1.0",
    string FieldOffset = "0",
    string PublicAwayTeamId = SportlinkClubMatchAanvraag.GeenTeamId)
{
    /// <summary>
    /// Sportlinks vaste "geen team"-ID (#1871): in Sportlink Clubs eigen frontend-bundle de constante waarmee
    /// een vrije-tekst-team wordt verstuurd, en live (2026-11-11) de waarde van <c>ClubMatchDefaults</c> voor
    /// <c>PublicHomeTeamId</c> en <c>PublicAwayTeamId</c>. Sportlink-breed, geen clubwaarde.
    /// </summary>
    public const string GeenTeamId = "T2010269033";
}
