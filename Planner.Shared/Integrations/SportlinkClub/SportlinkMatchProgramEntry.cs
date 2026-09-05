using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Eén rij uit <c>competition/match/MatchProgramOverview</c> — alleen de velden nodig voor de
/// #987/#1016-reverse-lookup (<see cref="ExternalMatchId"/> komt overeen met onze eigen
/// <c>his.matches.wedstrijdnummer</c>, zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §2.2).
/// </summary>
public sealed record SportlinkMatchProgramEntry
{
    [JsonPropertyName("externalMatchId")]
    public long ExternalMatchId { get; set; }

    [JsonPropertyName("publicMatchId")]
    public string PublicMatchId { get; set; } = "";
}
