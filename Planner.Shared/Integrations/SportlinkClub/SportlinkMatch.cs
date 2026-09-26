using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Wedstrijdgegevens uit Sportlink Club API, strikt beperkt tot niet-persoonsgebonden velden.
/// GEEN scheidsrechters-, officials- of spelersnamen — met PRECIES ÉÉN genoemde uitzondering
/// (#1340, eigenaarsbesluit 2026-09-26, herziening van deze grens): de RELATIECODE (een intern
/// Sportlink-identificatienummer, GEEN naam) van scheidsrechter/AR1/AR2 mag opgehaald en getoond
/// worden — zie <see cref="ScheidsrechterRelatieCode"/>/<see cref="Ar1RelatieCode"/>/
/// <see cref="Ar2RelatieCode"/> hieronder.
/// <para>
/// <b>De grens is hard en smal.</b> <see cref="SportlinkMatchOfficial"/> modelleert daarom bewust
/// NIET de overige velden die <c>matchOfficials</c> blijkt te bevatten — een incident op
/// 2026-09-06 (zie <c>docs/SPORTLINK-WEB-EXTENSION.md</c> §5) toonde dat dit element ook naam,
/// geboortedatum en foto-URL van de official bevat. Een toekomstige uitbreiding van dit model met
/// een van die velden is een NIEUW AVG-besluit, geen uitbreiding van #1340.
/// </para>
/// <para>
/// <b>Openstaande DPO-vraag, nog NIET beantwoord door de eigenaar</b> (rechtsgrond, bewaartermijn,
/// of dit gelogd wordt): zie het VOORSTEL in <c>docs/SPORTLINK-WEB-EXTENSION.md</c>, sectie
/// "Openstaande DPO-vraag — relatiecode-prefill (#1340)". Behandel de relatiecode tot die
/// bevestiging als een indirect persoonsgegeven.
/// </para>
/// </summary>
public sealed record SportlinkMatch
{
    [JsonPropertyName("publicMatchId")]
    public string PublicMatchId { get; set; } = "";

    // Live vastgesteld (2026-09-06): Sportlink levert dit veld als JSON-getal, niet als string —
    // zie FlexibleStringJsonConverter voor waarom AllowReadingFromString dit niet al opving.
    [JsonPropertyName("externalMatchId")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string ExternalMatchId { get; set; } = "";

    // Live vastgesteld (2026-09-06, vervolg op #1036): Sportlink levert dit veld genest
    // ({Date, StartTime, DateTime}), niet als losse ISO-string — zie MatchDateJsonConverter.
    [JsonPropertyName("matchDate")]
    [JsonConverter(typeof(MatchDateJsonConverter))]
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

    // Live vastgesteld (2026-09-06, netwerktrace door de eigenaar): nodig om de juiste
    // kleedkamer-/veld-identifiervorm te bouwen ("{FacilityId}-DRESSINGROOM-{n}" resp.
    // "{FacilityId}-OUTDOOR_FIELD-{n}") — geen persoonsgegeven, puur een accommodatiecode.
    [JsonPropertyName("matchField")]
    public SportlinkMatchField? MatchField { get; set; }

    // #1339: het huidige veld(deel) van de wedstrijd — al live bevestigd aanwezig in dezelfde
    // Match-GET-respons (2026-09-06, #1047, zie SportlinkClubClient.SportlinkFieldRaw, tot nu toe
    // uitsluitend intern gebruikt bij een veldwijziging). Toevoegen aan dit publieke read-model
    // zodat SportlinkMatchPanel het huidige FieldId/FieldSize kan voorafvullen in plaats van leeg
    // te laten — geen persoonsgegeven, puur een accommodatiecode/-afmeting.
    [JsonPropertyName("field")]
    public SportlinkMatchFieldSnapshot? Field { get; set; }

    // #1340 (eigenaarsbesluit 2026-09-26, herziening van de klasse-brede AVG-grens hierboven):
    // toegevoegd zodat de drie relatiecode-eigenschappen hieronder een official per positie
    // kunnen opzoeken. Zie SportlinkMatchOfficial voor de harde grens (uitsluitend positie +
    // relatiecode gemodelleerd, geen enkel ander veld uit dit element).
    [JsonPropertyName("matchOfficials")]
    public List<SportlinkMatchOfficial>? MatchOfficials { get; set; }

    /// <summary>Relatiecode van de scheidsrechter (positie <c>"Referee"</c> — zelfde ONBEVESTIGDE
    /// positieaanname als <see cref="SportlinkOfficialToewijzing"/>/#994), of <c>null</c> als
    /// (nog) niet toegewezen of onbekend. #1340: bewust géén naam — zie klasse-comment.</summary>
    [JsonPropertyName("scheidsrechterRelatieCode")]
    public string? ScheidsrechterRelatieCode => VindOfficialRelatieCode("Referee");

    /// <summary>Relatiecode van de eerste assistent-scheidsrechter (positie
    /// <c>"AssistantReferee1"</c>). Zie <see cref="ScheidsrechterRelatieCode"/>.</summary>
    [JsonPropertyName("ar1RelatieCode")]
    public string? Ar1RelatieCode => VindOfficialRelatieCode("AssistantReferee1");

    /// <summary>Relatiecode van de tweede assistent-scheidsrechter (positie
    /// <c>"AssistantReferee2"</c>). Zie <see cref="ScheidsrechterRelatieCode"/>.</summary>
    [JsonPropertyName("ar2RelatieCode")]
    public string? Ar2RelatieCode => VindOfficialRelatieCode("AssistantReferee2");

    private string? VindOfficialRelatieCode(string officialPosition) =>
        MatchOfficials?.FirstOrDefault(o =>
            string.Equals(o.OfficialPosition, officialPosition, StringComparison.Ordinal))?.RelatieCode;
}

/// <summary>
/// Eén regel uit Sportlinks <c>matchOfficials</c>-array in de Match-GET-respons (#1340). Modelleert
/// UITSLUITEND de twee velden die <see cref="SportlinkMatch"/> nodig heeft om een relatiecode per
/// positie op te zoeken — geen enkel ander veld uit dit element (naam, geboortedatum, foto-URL —
/// zie het incident van 2026-09-06, <c>docs/SPORTLINK-WEB-EXTENSION.md</c> §5) wordt hier
/// gemodelleerd, ook al staan die velden in de rauwe JSON.
/// </summary>
public sealed record SportlinkMatchOfficial
{
    // Zelfde ONBEVESTIGDE aanname als de schrijfkant (SportlinkOfficialToewijzing/#994): "Referee"/
    // "AssistantReferee1"/"AssistantReferee2". "OfficialPosition" als veldnaam is al eerder als
    // aanname vastgelegd in docs/SPORTLINK-WEB-EXTENSION.md §6.2 ("afgeleid uit OfficialPosition
    // zoals dat terugkomt in GET .../MatchOfficials") — dit is dus GEEN nieuwe gok, alleen de
    // eerste keer dat die aanname in een C#-model landt.
    [JsonPropertyName("OfficialPosition")]
    public string? OfficialPosition { get; set; }

    // TODO(#1340, owner-verificatie vóór merge): dit veldnaam is NOOIT live geverifieerd tegen de
    // Sportlink-API — controleer via de acceptatie-worktree vóór deze PR gemerged wordt.
    [JsonPropertyName(RelatieCodeJsonVeldnaam)]
    public string? RelatieCode { get; set; }

    /// <summary>ENIGE plek met de aanname voor het relatiecode-veldnaam (#1340) — zie de TODO
    /// hierboven. Een <c>const</c> i.p.v. een letterlijke string rechtstreeks in het attribuut,
    /// zodat een toekomstige correctie een eenregelige wijziging is.</summary>
    internal const string RelatieCodeJsonVeldnaam = "RelatieCode";
}

/// <summary>Huidig veld(deel) van een wedstrijd — geen persoonsgegevens, alleen accommodatiecodes.
/// Zelfde vorm als het interne <c>SportlinkClubClient.SportlinkFieldRaw</c> dat al voor de
/// veldwijziging gebruikt wordt; hier publiek zodat het read-model (<see cref="SportlinkMatch"/>)
/// het kan tonen.</summary>
public sealed record SportlinkMatchFieldSnapshot
{
    [JsonPropertyName("fieldId")]
    public string? FieldId { get; set; }

    // Live vastgesteld (2026-09-06, #1047-vervolg): komt in de Match-GET-respons als JSON-getal
    // terug, terwijl UpdateMatchDetails' eigen MatchData.FieldSize als string verwacht wordt.
    [JsonPropertyName("fieldSize")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? FieldSize { get; set; }

    [JsonPropertyName("fieldOffset")]
    public int? FieldOffset { get; set; }
}

/// <summary>Facility-gegevens van een wedstrijd — geen persoonsgegevens, alleen accommodatiecodes.</summary>
public sealed record SportlinkMatchField
{
    [JsonPropertyName("facilityId")]
    public string? FacilityId { get; set; }

    [JsonPropertyName("subFacilityId")]
    public string? SubFacilityId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
