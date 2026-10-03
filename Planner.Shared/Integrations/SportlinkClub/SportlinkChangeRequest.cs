using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Eén inkomend wijzigingsverzoek (#996, epic #986) —
/// <c>competition/match/changerequest/MatchChangeRequests</c>. Veldnamen letterlijk uit
/// Sportlinks eigen bundle-code (issue #996), maar de omringende responsvorm (kale array vs.
/// genest onder een property) is NIET live bevestigd — zie
/// <see cref="ISportlinkClubClient.GetChangeRequestsAsync"/> voor hoe daarmee wordt omgegaan.
/// Bevat bewust geen persoonsgegevens buiten wat al in <see cref="SportlinkMatch"/> zou staan.
/// </summary>
public sealed record SportlinkChangeRequest
{
    [JsonPropertyName("publicMatchId")]
    public string PublicMatchId { get; set; } = "";

    [JsonPropertyName("publicRequestId")]
    public string PublicRequestId { get; set; } = "";

    /// <summary>
    /// Sportlinks <c>ChangeRequestStatus</c> (veldnaam en enum bevestigd uit Sportlinks eigen
    /// frontend-bundle, #1439): APPROVED, CONFIRM_AWAY, CONFIRM_HOME, CONFIRM_UNION, DENIED,
    /// MATCH_FINALIZED, REVOKED. De eerdere aanname <c>requestStatus</c>/<c>CONFIRM</c> was fout en
    /// leverde altijd een lege waarde op. De C#-naam blijft <c>RequestStatus</c> (stabiel API-contract).
    /// </summary>
    [JsonPropertyName("ChangeRequestStatus")]
    public string RequestStatus { get; set; } = "";

    /// <summary>True = inkomend verzoek (de tegenstander vraagt ons), false = uitgaand (wij vragen).</summary>
    [JsonPropertyName("IsIncomingRequest")]
    public bool? IsIncomingRequest { get; set; }

    [JsonPropertyName("requestData")]
    public SportlinkChangeRequestData? RequestData { get; set; }

    /// <summary>
    /// Sportlinks wedstrijdnummer — de kolom "Wedstrijdnr." van Sportlinks eigen
    /// wijzigingsverzoekentabel (veldnaam uit de frontend-bundle, #1464). Flexibel gelezen: het
    /// <c>Match</c>-endpoint levert hetzelfde veld als JSON-getal.
    /// </summary>
    [JsonPropertyName("ExternalMatchId")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? ExternalMatchId { get; set; }

    /// <summary>Thuisteam zoals Sportlink het toont (<c>HomeTeam.TeamName</c>, #1464).</summary>
    [JsonPropertyName("HomeTeam")]
    public SportlinkChangeRequestTeam? HomeTeam { get; set; }

    /// <summary>Uitteam zoals Sportlink het toont (<c>AwayTeam.TeamName</c>, #1464).</summary>
    [JsonPropertyName("AwayTeam")]
    public SportlinkChangeRequestTeam? AwayTeam { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("remarks")]
    public string? Remarks { get; set; }
}

/// <summary>Team in een wijzigingsverzoek — alleen de naam; het logo wordt niet overgenomen (#1464).</summary>
public sealed record SportlinkChangeRequestTeam
{
    [JsonPropertyName("TeamName")]
    public string? TeamName { get; set; }
}

/// <summary>Huidige vs. gevraagde datum/tijd/accommodatie — velden letterlijk uit issue #996.</summary>
public sealed record SportlinkChangeRequestData
{
    [JsonPropertyName("currentDate")]
    public string? CurrentDate { get; set; }

    [JsonPropertyName("currentStartTime")]
    public string? CurrentStartTime { get; set; }

    [JsonPropertyName("currentFacilityName")]
    public string? CurrentFacilityName { get; set; }

    [JsonPropertyName("currentSubFacilityName")]
    public string? CurrentSubFacilityName { get; set; }

    [JsonPropertyName("requestedDate")]
    public string? RequestedDate { get; set; }

    [JsonPropertyName("requestedStartTime")]
    public string? RequestedStartTime { get; set; }

    [JsonPropertyName("requestedFacilityName")]
    public string? RequestedFacilityName { get; set; }

    [JsonPropertyName("requestedSubFacilityName")]
    public string? RequestedSubFacilityName { get; set; }
}
