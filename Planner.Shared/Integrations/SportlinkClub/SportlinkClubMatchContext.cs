using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Alles wat Sportlink Clubs eigen formulier "Voeg wedstrijd toe" ophaalt voordat een
/// oefenwedstrijd kan worden aangemaakt (#1427) — vier read-only GETs onder
/// <c>competition/match/clubmatch/</c>. <b>Live vastgesteld 01-10-2026</b> met een
/// console-trace door de eigenaar (geen tokens, alleen vorm en voorbeeldwaarden); de veldnamen
/// hieronder zijn die van de echte respons, niet langer aangenomen.
/// </summary>
/// <param name="Defaults"><c>ClubMatchDefaults</c> — o.a. het eerstvolgende wedstrijdnummer;
/// verandert na elke aanmaak en wordt daarom nooit gecachet.</param>
/// <param name="Teams"><c>PickListsTeams.ClubTeams</c>.</param>
/// <param name="Facilities"><c>PickListsLocation.Facilities</c>.</param>
/// <param name="Activities"><c>PickListsMatchInformation.Activities</c> ("Spelactiviteit").</param>
/// <param name="AgeClasses"><c>PickListsMatchInformation.AgeClasses</c> ("Leeftijdscategorie").</param>
public sealed record SportlinkClubMatchContext(
    SportlinkClubMatchDefaults Defaults,
    IReadOnlyList<SportlinkClubTeam> Teams,
    IReadOnlyList<SportlinkClubFacility> Facilities,
    IReadOnlyList<SportlinkClubActivity> Activities,
    IReadOnlyList<SportlinkClubAgeClass> AgeClasses);

/// <summary><c>ClubMatchDefaults</c>: de waarden waarmee Sportlinks eigen formulier opent.</summary>
public sealed record SportlinkClubMatchDefaults(
    long? ExternalMatchId,
    string? Description,
    string? PublicHomeTeamId,
    string? SportIdTag,
    bool? IsHomeMatch,
    string? FacilityId,
    string? SubFacilityId,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? FieldSize,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? FieldOffset,
    string? AgeClassCode);

/// <summary>Eén team uit <c>PickListsTeams.ClubTeams</c>. <c>Id</c> is het <c>T…</c>-ID dat
/// <c>ClubMatch</c> als <c>PublicHomeTeamId</c> verwacht — niet de numerieke dataservice-teamcode.</summary>
public sealed record SportlinkClubTeam(
    string? Id,
    string? TeamName,
    string? Description,
    string? ExternalSportId,
    string? SportTag);

/// <summary>Eén accommodatie uit <c>PickListsLocation.Facilities</c>.</summary>
public sealed record SportlinkClubFacility(
    string? FacilityId,
    string? NormalizedName,
    bool IsDefault,
    IReadOnlyList<SportlinkClubField>? Fields);

/// <summary>Eén veld van een accommodatie. Het <c>SubFacilityId</c> volgt géén vast patroon uit het
/// veldnummer (live: "veld 5" = <c>…-OUTDOOR_FIELD-6</c>), dus altijd op naam opzoeken.</summary>
public sealed record SportlinkClubField(string? SubFacilityId, string? Name, string? NormalizedName);

/// <summary>Eén spelactiviteit, bijv. <c>IdTag</c> "SOCCER-VE-AL/FRIDAY" = "Veld - Vrijdag".</summary>
public sealed record SportlinkClubActivity(string? IdTag, string? Description);

/// <summary>Eén leeftijdscategorie, bijv. <c>Id</c> "001" = "Senioren (M)".</summary>
public sealed record SportlinkClubAgeClass(string? Id, string? Description);
