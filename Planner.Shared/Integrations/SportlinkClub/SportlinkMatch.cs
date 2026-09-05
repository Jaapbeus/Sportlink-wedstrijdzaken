using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Wedstrijdgegevens uit Sportlink Club API, strikt beperkt tot niet-persoonsgebonden velden.
/// GEEN scheidsrechters-, officials- of spelersnamen.
/// </summary>
public sealed record SportlinkMatch
{
    [JsonPropertyName("publicMatchId")]
    public string PublicMatchId { get; set; } = "";

    [JsonPropertyName("externalMatchId")]
    public string ExternalMatchId { get; set; } = "";

    [JsonPropertyName("matchDate")]
    public DateTimeOffset MatchDate { get; set; }

    [JsonPropertyName("matchStatus")]
    public string MatchStatus { get; set; } = "";

    [JsonPropertyName("isHomeMatch")]
    public bool IsHomeMatch { get; set; }

    [JsonPropertyName("isCanceledMatch")]
    public bool IsCanceledMatch { get; set; }

    [JsonPropertyName("isConceptMatch")]
    public bool IsConceptMatch { get; set; }

    [JsonPropertyName("taskStatus")]
    public string? TaskStatus { get; set; }

    [JsonPropertyName("isEditFieldAllowed")]
    public bool IsEditFieldAllowed { get; set; }

    [JsonPropertyName("isAssignDressingRoomsAllowed")]
    public bool IsAssignDressingRoomsAllowed { get; set; }

    [JsonPropertyName("isAssignOfficialsAllowed")]
    public bool IsAssignOfficialsAllowed { get; set; }

    [JsonPropertyName("isEditFieldSidePanelAllowed")]
    public bool IsEditFieldSidePanelAllowed { get; set; }

    [JsonPropertyName("isAddScoreAllowed")]
    public bool IsAddScoreAllowed { get; set; }
}
