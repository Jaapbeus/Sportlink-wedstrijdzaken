using System.Text.Json.Serialization;

namespace BlazorAdmin.Models;

/// <summary>#992: respons van een schrijvende Sportlink-actie (kleedkamers e.v.). Bij een
/// inhoudelijke weigering door Sportlink zelf (HTTP 422) staat <see cref="Violations"/> gevuld —
/// <c>ApiResult.ErrorMessage</c> bevat dan de generieke foutmelding, dit veld de details.</summary>
public class SportlinkMutatieResultaatDto
{
    public bool IsSuccess { get; set; }
    public List<string>? Violations { get; set; }

    /// <summary>#998: true als dry-run actief was — niets is echt naar Sportlink verstuurd,
    /// de aanroep is alleen gesimuleerd en gelogd.</summary>
    public bool IsDryRun { get; set; }

    /// <summary>#994: true als de PUT/POST werd overgeslagen door de code-niveau forceDryRun-lock
    /// (mutatie waarvan de requestbody nog niet live bevestigd is) — ONAFHANKELIJK van de
    /// club-instelling sportlinkDryRun. Als dit true is, is IsDryRun ook altijd true.</summary>
    public bool IsForcedDryRun { get; set; }

    /// <summary>#997: door Sportlink teruggegeven PublicMatchId van een NIEUW aangemaakte
    /// oefenwedstrijd — null voor elke andere mutatie en ook null zolang IsForcedDryRun/IsDryRun
    /// de aanroep simuleerde (in de praktijk dus altijd null zolang de ClubMatch-code-lock actief
    /// is).</summary>
    public string? PublicMatchId { get; set; }
}

// NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
// beide PUT's en de bevestigingsvlag vastleggen). Deze DTO's horen uitsluitend bij stap 1
// (valideren) — er bestaat bewust geen stap 2 (bevestigen): geen endpoint, geen UI-knop.

/// <summary>#995: respons van het wijzigingsverzoek-endpoint (stap 1, valideren) — spiegelt
/// Planner.Shared.Integrations.SportlinkClub.SportlinkMatchChangeRequestResult. Houd deze twee
/// synchroon bij een contractwijziging.</summary>
public class SportlinkMatchWijzigingsverzoekResultaatDto
{
    public SportlinkMutatieResultaatDto? Mutatie { get; set; }
    public SportlinkMatchChangeValidatieDto? Validatie { get; set; }
}

public class AutoPlanWedstrijdItemDto
{
    public long? WedstrijdCode { get; set; }
    public string Wedstrijd { get; set; } = "";
    public string TeamNaam { get; set; } = "";
    public string? LeeftijdsCategorie { get; set; }
    public string? Competitiesoort { get; set; }
    public int DuurMinuten { get; set; }
    public decimal Veldafmeting { get; set; }
    public string? HuidigeVeld { get; set; }
    public string? HuidigeTijd { get; set; }
    public bool HeeftVeld { get; set; }
    public bool HeeftTijd { get; set; }
    public int? OptimaalVeldNummer { get; set; }
    public string? OptimaalVeldNaam { get; set; }
    public string? OptimaalVeld { get; set; }
    public string? OptimaalTijd { get; set; }
    // "nieuw-slot" | "wijziging" | "ongewijzigd" | "niet-inplanbaar"
    // Zegt alleen of de planner verplaatst t.o.v. de huidige stand — NIET of de voorkeurstijd
    // gehaald is. Dat staat in VoorkeurStatus (#666).
    public string Status { get; set; } = "ongewijzigd";
    public string? NietInplanbaaarReden { get; set; }
    // Voorkeurstijd (null = geen voorkeur én geen leeftijdsdefault geconfigureerd)
    public string? VoorkeurTijd { get; set; }
    public int? VoorkeurAfwijkingMinuten { get; set; }
    // "regel" | "team" | "leeftijd" | null — waar de voorkeurstijd uit komt (#666)
    public string? VoorkeurBron { get; set; }
    // "op-tijd" | "kleine-afwijking" | "grote-afwijking" | "geen-voorkeur"
    public string VoorkeurStatus { get; set; } = "geen-voorkeur";
    public int? VoorkeurVeldNummer { get; set; }
    public bool? VoorkeurVeldToegepast { get; set; }
    // #1430: ruwe actieve teamregels BufferVoor/BufferNa (null = geen regel). De effectieve buffer
    // bepaalt PlanningBufferRegels (gedeeld met de planner) met de op dat moment ingestelde buffer.
    public int? TeamBufferVoor { get; set; }
    public int? TeamBufferNa { get; set; }
    public bool SportlinkLeidend { get; set; }
}

public class AutoPlanToepassenRequestDto
{
    public string Datum { get; set; } = "";
    public int? BufferMinuten { get; set; }
}
