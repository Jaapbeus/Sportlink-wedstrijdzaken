namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Eén item uit een Sportlink-picklist (#997) — id + weergavenaam. ONBEVESTIGD: de werkelijke
/// JSON-veldnamen per picklist-endpoint zijn nooit met een netwerktrace gezien; <see
/// cref="ISportlinkClubClient.GetClubMatchPickListsAsync"/> probeert daarom defensief een paar
/// aannemelijke veldnamen (zie <c>SportlinkClubClient.ParsePickListItem</c>) in plaats van een
/// strikt contract af te dwingen.
/// </summary>
public sealed record SportlinkPickListItem(string? Id, string? Naam);

/// <summary>
/// Gecombineerd resultaat van de twee ondersteunende picklist-GETs die in deze ronde bewust WEL zijn
/// aangesloten (#997, bewust beperkte scope): <c>clubmatch/PickListsTeams</c> en
/// <c>clubmatch/PickListsLocation</c>. Read-only en persoonsgegevensvrij (teams/locaties, geen
/// personen). De overige drie ondersteunende endpoints uit het issue
/// (<c>ClubMatchDefaults</c>, <c>PickListsMatchInformation</c>, <c>codetable/AgeClassList</c>) zijn
/// bewust NIET aangesloten — zie de PR-beschrijving.
/// </summary>
public sealed record SportlinkClubMatchPickLists(
    IReadOnlyList<SportlinkPickListItem> Teams,
    IReadOnlyList<SportlinkPickListItem> Locations);
