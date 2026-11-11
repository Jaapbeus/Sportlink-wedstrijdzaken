namespace BlazorAdmin.Models;

/// <summary>#996: één inkomend wijzigingsverzoek — spiegelt
/// Planner.Shared.Integrations.SportlinkClub.SportlinkChangeRequest (gedeelde DTO). Houd deze twee
/// synchroon bij een contractwijziging.</summary>
public class SportlinkChangeRequestDto
{
    public string? PublicMatchId { get; set; }
    public string? PublicRequestId { get; set; }
    public string? RequestStatus { get; set; }

    /// <summary>#1439: true = inkomend (tegenstander vraagt ons), false = uitgaand, null = onbekend.</summary>
    public bool? IsIncomingRequest { get; set; }

    /// <summary>#1439: OPEN, ACCEPTED, DENIED, REVOKED of UNKNOWN — zie SportlinkChangeRequestStatusGroep.</summary>
    public string? StatusGroep { get; set; }

    /// <summary>#1464: Sportlinks eigen wedstrijdnummer en teamnamen — terugval als
    /// <see cref="Wedstrijd"/> ontbreekt.</summary>
    public string? ExternalMatchId { get; set; }
    public string? Thuisteam { get; set; }
    public string? Uitteam { get; set; }
    public SportlinkChangeRequestDataDto? RequestData { get; set; }
    public string? Reason { get; set; }
    public string? Remarks { get; set; }

    /// <summary>#1111: onze eigen wedstrijdcontext (uit his.matches via de PublicMatchId-cache),
    /// null als de wedstrijd (nog) niet gecachet is. Spiegelt
    /// FunctionApp.Postgres.Sportlink.SportlinkWedstrijdContext.</summary>
    public SportlinkWedstrijdContextDto? Wedstrijd { get; set; }
}

public class SportlinkWedstrijdContextDto
{
    public long Wedstrijdcode { get; set; }
    public long? Wedstrijdnummer { get; set; }
    public string? Thuisteam { get; set; }
    public string? Uitteam { get; set; }
    public string? Datum { get; set; }
    public string? Tijd { get; set; }
    public string? Accommodatie { get; set; }
}

public class SportlinkChangeRequestDataDto
{
    public string? CurrentDate { get; set; }
    public string? CurrentStartTime { get; set; }
    public string? CurrentFacilityName { get; set; }
    public string? CurrentSubFacilityName { get; set; }
    public string? RequestedDate { get; set; }
    public string? RequestedStartTime { get; set; }
    public string? RequestedFacilityName { get; set; }
    public string? RequestedSubFacilityName { get; set; }
}

/// <summary>#995: ONBEVESTIGD — geparsed uit Sportlinks <c>ConfirmationNeeded</c>-veld, nooit met
/// een netwerktrace gezien. <c>null</c> zolang de code-lock actief is (zie
/// SportlinkWedstrijdDetails.UpdateMatchDetailsChangeRequestLiveBevestigd).</summary>
public class SportlinkMatchChangeValidatieDto
{
    public bool ConfirmationNeeded { get; set; }
    public List<string>? ValidationResultMessages { get; set; }
    public bool HasBlockingMessages { get; set; }

    // #1320: vier toplevel booleans, gezien bij de oefenwedstrijd-trace van 2026-09-26 — GEEN
    // bewijs voor de verplichte-wijzigingsverzoek-vorm. Spiegelt
    // Planner.Shared.Integrations.SportlinkClub.SportlinkMatchChangeValidatie.
    public bool? IsSuccess { get; set; }
    public bool? IsMatchChangeRequestMandatory { get; set; }
    public bool? IsOwnFacility { get; set; }
    public bool? IsForceUpdate { get; set; }
}

/// <summary>#1320: diagnostiektrace van één #995-stap-1-poging (eigenaar-gestuurde productieproef)
/// — spiegelt Planner.Shared.Integrations.SportlinkClub.SportlinkMatchWijzigingsverzoekTrace. Houd
/// deze twee synchroon bij een contractwijziging.</summary>
public class SportlinkMatchWijzigingsverzoekTraceDto
{
    public SportlinkMatchWijzigingsverzoekResultaatDto? Resultaat { get; set; }
    public long? AuditId { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? Endpoint { get; set; }
    public string? HttpMethode { get; set; }
    public DateTime TijdstipUtc { get; set; }
}
