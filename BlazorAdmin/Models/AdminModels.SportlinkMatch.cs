namespace BlazorAdmin.Models;

/// <summary>#991: read-only Sportlink-paneel per wedstrijd. Spiegelt grotendeels
/// Planner.Shared.Integrations.SportlinkClub.SportlinkMatch (gedeelde DTO, #991/#998) — houd deze
/// twee synchroon bij een contractwijziging. Sinds #1339 bevat de respons ook <see cref="FieldId"/>/
/// <see cref="FieldSize"/> (uit SportlinkMatch.Field) en de server-berekende
/// <see cref="VeldOpties"/>/<see cref="SubpositieOpties"/> (géén Sportlink-veld, zie
/// SportlinkMatchFunction.BouwPaneelResponse in beide tiers).</summary>
public class SportlinkMatchInfoDto
{
    public string? PublicMatchId { get; set; }
    public string? ExternalMatchId { get; set; }
    public DateTimeOffset? MatchDate { get; set; }
    public string? MatchStatus { get; set; }
    public bool IsHomeMatch { get; set; }
    public bool IsCanceledMatch { get; set; }
    public bool IsConceptMatch { get; set; }
    public string? TaskStatus { get; set; }
    public bool IsEditFieldAllowed { get; set; }
    public bool IsAssignDressingRoomsAllowed { get; set; }
    public bool IsAssignOfficialsAllowed { get; set; }
    public bool IsEditFieldSidePanelAllowed { get; set; }
    public bool IsAddScoreAllowed { get; set; }

    // #1339: huidig veld (prefill) + voorstellen om een ander veld te kiezen op onze eigen
    // veldnaam i.p.v. Sportlinks FieldId-formaat te moeten kennen. Zie SportlinkFieldIdBuilder
    // (Planner.Shared) voor hoe de voorstellen berekend worden — hier alleen het draaddata-model.
    public string? FieldId { get; set; }
    public string? FieldSize { get; set; }
    public List<SportlinkVeldOptieDto>? VeldOpties { get; set; }
    public List<SportlinkSubpositieOptieDto>? SubpositieOpties { get; set; }

    // #1341: per-club, per-rol instelbare zichtbaarheid — combineer altijd met de bijbehorende
    // Sportlink-eigen IsXAllowed-vlag hierboven (beide moeten waar zijn). Server is leidend: dit
    // is uitsluitend UX om een knop niet te tonen die toch een 409 zou geven.
    public bool KleedkamersFeatureToegestaan { get; set; }
    public bool ScheidsrechterFeatureToegestaan { get; set; }
    public bool VeldFeatureToegestaan { get; set; }

    // #1400: geen FeatureKey-toggle (het wijzigingsverzoek datum/tijd/accommodatie heeft er bewust
    // geen, zie SportlinkRolFeature.VoorMutatieSoort) — puur de rolcheck (admin of Wedstrijdzaken),
    // want Planning/het Sportlink-paneel is sinds #1400 generiek zichtbaar voor elke ingelogde
    // gebruiker terwijl wijzigen die rol vereist.
    public bool MagWijzigen { get; set; }

    // #1340 (VOORSTEL — DPO-vraag nog niet bevestigd door de eigenaar, zie
    // docs/SPORTLINK-WEB-EXTENSION.md): relatiecode van de huidige official per positie, alleen
    // gevuld als Sportlink al een official had toegewezen. De server nult deze drie velden altijd
    // naar null als ScheidsrechterFeatureToegestaan hierboven false is (SportlinkRolFeature.
    // VoegToestemmingenToe) — dus géén extra client-side gate nodig, maar wél nooit weglaten.
    public string? ScheidsrechterRelatieCode { get; set; }
    public string? Ar1RelatieCode { get; set; }
    public string? Ar2RelatieCode { get; set; }
}

/// <summary>#1339: spiegelt Planner.Shared.Integrations.SportlinkClub.SportlinkVeldOptie.</summary>
public class SportlinkVeldOptieDto
{
    public int VeldNummer { get; set; }
    public string VeldNaam { get; set; } = "";
    public string? VoorstelFieldId { get; set; }
}

/// <summary>#1339: spiegelt Planner.Shared.Integrations.SportlinkClub.SportlinkSubpositieOptie.</summary>
public class SportlinkSubpositieOptieDto
{
    public string? Subpositie { get; set; }
    public string VoorstelFieldSize { get; set; } = "1.0";
}

/// <summary>#989: respons van de lichtgewicht PublicMatchId-only endpoint (deep-link-knop).</summary>
public class SportlinkPublicMatchIdDto
{
    public string? PublicMatchId { get; set; }
}
