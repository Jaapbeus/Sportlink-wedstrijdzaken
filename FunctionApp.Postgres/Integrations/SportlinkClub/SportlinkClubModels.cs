using System.Text.Json;
using System.Text.Json.Serialization;

namespace FunctionApp.Postgres.Integrations.SportlinkClub;

/// <summary>
/// Read-only DTO's voor de club.sportlink.com Navajo-API (#991, epic #986). Bewust géén
/// strongly-typed vorm voor <c>Field</c>/<c>Facility</c>/<c>MatchDressingRooms</c> — de exacte
/// nested-JSON-vorm van die objecten is niet live geverifieerd (zie
/// docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md #991: "exacte veldnamen onzeker" voor
/// vergelijkbare velden). Een gefabriceerde klasse-vorm voor ongeverifieerde data zou een
/// ongeteste aanname als feit vastleggen; in plaats daarvan geven we de ruwe JSON door.
/// </summary>
public sealed class SportlinkMatchInfo
{
    public string? PublicMatchId { get; set; }
    public string? MatchDate { get; set; }
    public string? MatchStatus { get; set; }
    public bool IsCanceledMatch { get; set; }
    public bool IsConceptMatch { get; set; }
    public bool IsHomeMatch { get; set; }
    public bool IsEditFieldAllowed { get; set; }
    public bool IsEditFieldSidePanelAllowed { get; set; }
    public bool IsAssignDressingRoomsAllowed { get; set; }
    public bool IsAssignOfficialsAllowed { get; set; }
    public bool IsAddScoreAllowed { get; set; }
    public bool IsEditFieldOffsetAllowed { get; set; }
    public bool IsEditFieldSizeAllowed { get; set; }
    public string[] TaskStatus { get; set; } = Array.Empty<string>();

    public JsonElement? Field { get; set; }
    public JsonElement? Facility { get; set; }
    public JsonElement? MatchDressingRooms { get; set; }
}

/// <summary>Eén rij uit <c>competition/match/MatchProgramOverview</c> — alleen de velden nodig
/// voor de #987-reverse-lookup (<see cref="ExternalMatchId"/> = onze eigen <c>wedstrijdnummer</c>,
/// zie his.matches).</summary>
internal sealed class MatchProgramOverviewItem
{
    public long ExternalMatchId { get; set; }
    public string? PublicMatchId { get; set; }
}

internal sealed class SportlinkTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_expires_in")]
    public int RefreshExpiresIn { get; set; }
}

/// <summary>Geen rij in <c>public.sportlinkservicetokens</c> voor deze rol/club — nog nooit
/// geregistreerd via de admin-PUT.</summary>
public sealed class SportlinkNietGekoppeldException(string rolNaam) : Exception(
    $"Geen Sportlink-koppeling gevonden voor rol '{rolNaam}' — registreer eerst een refresh-token via Instellingen.");

/// <summary>Het opgeslagen refresh_token is door Sportlink geweigerd (<c>invalid_grant</c>) —
/// meestal omdat het te lang niet gebruikt is (Sportlink-onderzoek: refresh_expires_in = 6 uur).</summary>
public sealed class SportlinkTokenVerlopenException(string rolNaam) : Exception(
    $"De Sportlink-koppeling voor rol '{rolNaam}' is verlopen — registreer een nieuw refresh-token via Instellingen.");
