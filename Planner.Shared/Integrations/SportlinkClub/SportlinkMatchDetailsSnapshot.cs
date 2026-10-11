using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

// ── Rauwe modellen voor UpdateMatchDetails — uitsluitend intern, nooit publiek. Elk veld hier
// is live bevestigd aanwezig in Sportlinks eigen Match-GET-respons (2026-09-06, #1047),
// op naam gecontroleerd zonder ooit persoonsgegevens (MatchOfficials) te loggen.
// Zichtbaarheid `internal` (niet `private`, sinds #995) zodat Planner.Shared.Tests een snapshot
// rechtstreeks kan opbouwen voor een body-test op BuildMatchChangeRequestBody — dezelfde reden
// als BuildMatchOfficialsBody hierboven al `internal` is. ──

internal sealed record SportlinkMatchDetailsSnapshot(
    string? AgeClassCode,
    int? Duration,
    string? MatchStatus,
    [property: JsonConverter(typeof(FlexibleLongJsonConverter))] long? ExternalMatchId,
    SportlinkMatchDateRaw? MatchDate,
    SportlinkFieldRaw? Field,
    SportlinkMatchField? MatchField,
    SportlinkSportRaw? Sport,
    SportlinkTeamsRaw? Teams,
    SportlinkResultRaw? Result,
    SportlinkMatchDetailsFieldsRaw? MatchDetails);

internal sealed record SportlinkMatchDateRaw(string? Date, string? StartTime);

// Live vastgesteld (2026-09-06, #1047-vervolg): Field.FieldSize komt in de Match-GET-respons
// als JSON-getal terug, terwijl UpdateMatchDetails' eigen MatchData.FieldSize als string
// verwacht wordt (zie de netwerktrace) — zelfde wisselvallige-veldtype-patroon als #1036.
internal sealed record SportlinkFieldRaw(
    string? FieldId,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? FieldSize,
    int? FieldOffset);

internal sealed record SportlinkSportRaw(string? IdTag);

internal sealed record SportlinkTeamRaw(string? TeamName, string? PublicTeamId);

internal sealed record SportlinkTeamsRaw(SportlinkTeamRaw? Home, SportlinkTeamRaw? Away);

internal sealed record SportlinkResultRaw(int? HomeScore, int? AwayScore);

internal sealed record SportlinkEditableFieldRaw(string? Value);

internal sealed record SportlinkMatchDetailsHomeRaw(SportlinkEditableFieldRaw? AssemblyTime);

internal sealed record SportlinkMatchDetailsFieldsRaw(
    SportlinkEditableFieldRaw? Description, SportlinkMatchDetailsHomeRaw? MatchDetailsHome);
