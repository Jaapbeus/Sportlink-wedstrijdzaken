using System.Text.Json.Serialization;

namespace BlazorAdmin.Models;

public class AppSettingsDto
{
    public string? ClubName { get; set; }
    public string? ClubCode { get; set; }
    public string? SportlinkApiUrl { get; set; }
    public int? SeasonStartMonth { get; set; }
    public string? Accommodatie { get; set; }
    public DateTime? LastSyncTimestamp { get; set; }
    public string? FetchSchedule { get; set; }
    public string? PlannerAfzenderNaam { get; set; }
    public string? CoordinatorNaam { get; set; }
    public string? CoordinatorFunctie { get; set; }
    public string? PlannerEmailAdres { get; set; }
    public int? HerplanDeadlineDagen { get; set; }
    public int? BufferMinuten { get; set; }
    public string? EmailVoetnoot { get; set; }
    public string? AccommodatiePlaats { get; set; }
    public double? AccommodatieLatitude { get; set; }
    public double? AccommodatieLongitude { get; set; }
    public string? FetchScheduleLeesbaar { get; set; }
    public List<string>? VolgendeMomenten { get; set; }
    public bool UseRealtimeApi { get; set; } = true;
    public bool KnvbPdfBijlageIngeschakeld { get; set; } = true;
    public string? KnvbStandaardRegio { get; set; }
    public bool SportlinkExtensionEnabled { get; set; }
}

/// <summary>#988: rol↔serviceaccount-koppelingsstatus, zie docs/ONDERZOEK-SPORTLINK-CLUB-SCHRIJFACTIES.md §6.</summary>
public class SportlinkExtensieRolDto
{
    public string RolNaam { get; set; } = "";
    public bool Gekoppeld { get; set; }
    public string? LaatstGekoppeldDoor { get; set; }
    public DateTime? LaatstGekoppeldOp { get; set; }
    public string? SportlinkAccountNaam { get; set; }
}

public class GeocodeResultDto
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string DisplayName { get; set; } = "";
}

/// <summary>#991: read-only Sportlink-paneel per wedstrijd. Spiegelt
/// Planner.Shared.Integrations.SportlinkClub.SportlinkMatch (gedeelde DTO, #991/#998) — houd deze
/// twee synchroon bij een contractwijziging.</summary>
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
}

public class SettingsUpdateDto
{
    public string? GewijzigdDoor { get; set; }
    public Dictionary<string, string?>? Velden { get; set; }
}

public class SettingsUpdateResultDto
{
    public string[]? GewijzigdeVelden { get; set; }
    public bool HerstartVereist { get; set; }
    public bool HerstartAutomatisch { get; set; }
    public string? Opmerking { get; set; }
    public string? FetchScheduleLeesbaar { get; set; }
    public List<string>? VolgendeMomenten { get; set; }
}

public class SyncStatusDto
{
    public DateTime? LastSyncTimestamp { get; set; }
    public string? FetchSchedule { get; set; }
    public string? Status { get; set; }
}

public class TemplateDto
{
    public int Id { get; set; }
    public string TemplateKey { get; set; } = "";
    public string Onderwerp { get; set; } = "";
    public string BodyTemplate { get; set; } = "";
    public bool Actief { get; set; }
    public string ClubCode { get; set; } = "";
}

public class VoorkeurTijdDto
{
    public int Id { get; set; }
    public string TeamNaam { get; set; } = "";
    public int DagVanWeek { get; set; }
    public string VoorkeurTijd { get; set; } = "";
    public int Prioriteit { get; set; } = 5;
    public bool Actief { get; set; } = true;
    public string? ClubCode { get; set; }
}

public class EmailLogDto
{
    public int Id { get; set; }
    public string? MessageId { get; set; }
    public string? Afzender { get; set; }
    public string? Onderwerp { get; set; }
    public DateTime OntvangstDatum { get; set; }
    public string? VerzoekType { get; set; }
    public string? Status { get; set; }
    public string? VerstuurdNaar { get; set; }
    public string? FoutMelding { get; set; }
}

public class EmailLogResponse
{
    public int Count { get; set; }
    public int Limit { get; set; }
    public List<EmailLogDto> Items { get; set; } = new();
}

public class TestEmailRequest
{
    public string? Onderwerp { get; set; }
    public string? Afzender { get; set; }
    public string? AfzenderNaam { get; set; }
    public string? Body { get; set; }
}

public class TestEmailResponse
{
    public bool DryRun { get; set; }
    public string? Opmerking { get; set; }
    public object? Classificatie { get; set; }
    public object? PlannerResponse { get; set; }
    public VoorbeeldAntwoord? VoorbeeldAntwoord { get; set; }
}

public class VoorbeeldAntwoord
{
    public string? Onderwerp { get; set; }
    public string? Body { get; set; }
}

public class UitgeslotenEmailAdresDto
{
    public int Id { get; set; }
    public string EmailAdres { get; set; } = "";
    public string? Omschrijving { get; set; }
    public bool Actief { get; set; } = true;
    public string ClubCode { get; set; } = "";
}

public class TeamRegelDto
{
    public int Id { get; set; }
    public string TeamNaam { get; set; } = "";
    public string RegelType { get; set; } = "";
    public int? WaardeMinuten { get; set; }
    public int? WaardeVeldNummer { get; set; }
    public string? WaardeTijd { get; set; }
    public int Prioriteit { get; set; }
    public bool Actief { get; set; }
    public string? Opmerking { get; set; }
    public string ClubCode { get; set; } = string.Empty;
}

// ── Teambegeleiding ──

public class TeambegeldingImportRequest
{
    public string CsvContent { get; set; } = "";
    public string? Bestandsnaam { get; set; }
}

public class TeambegeldingImportResultaat
{
    public int Rijen { get; set; }
    public List<string> Herkend { get; set; } = [];
    public List<string> Ontbreekt { get; set; } = [];
    public List<string> Waarschuwingen { get; set; } = [];
}

public class TeambegeleidingItem
{
    public string Naam { get; set; } = "";
    public string Teamrol { get; set; } = "";
    public string? Emailadres { get; set; }
    public string? Telefoonnummer { get; set; }
}

public class DoorsturenRequest
{
    public string TeamNaam { get; set; } = "";
    public string? Onderwerp { get; set; }
    public string Bericht { get; set; } = "";
    public string? Ontvangers { get; set; }
}

// ── Speeltijden ──

public class SpeeltijdDto
{
    public string Leeftijd { get; set; } = "";
    public decimal Veldafmeting { get; set; } = 1.00m;
    public int WedstrijdTotaal { get; set; }
    public int WedstrijdHelft { get; set; }
    public int WedstrijdRust { get; set; }

    /// <summary>
    /// Standaard voorkeurstijd "HH:mm" voor deze leeftijdscategorie (#666). Leeg = geen streeftijd;
    /// de planner gebruikt deze tijd voor teams zonder eigen rij in de voorkeurstijden.
    /// </summary>
    public string? StandaardVoorkeurTijd { get; set; }
}

// ── Dagplanning ──
// De DTO's voor het losse "klassiek optimaliseren"-pad (OptimaliseerRequestDto/ResponseDto,
// OptimalisatieSuggestieDto, VeldCapaciteitDto) zijn vervallen bij #666, samen met dat endpoint.
// Er is nu één optimalisatie: auto-plan.

// ── Auto-plan (#380) ──

public class AutoPlanRequestDto
{
    public string Datum { get; set; } = "";
    public int? BufferMinuten { get; set; }
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
}

public class AutoPlanResponseDto
{
    public string Datum { get; set; } = "";
    public int TotaalWedstrijden { get; set; }
    public int ZonderVeld { get; set; }
    public int ZonderTijd { get; set; }
    public int TeWijzigen { get; set; }
    public int NietInplanbaar { get; set; }
    public string? GeschatteEindTijd { get; set; }
    public List<AutoPlanWedstrijdItemDto> Wedstrijden { get; set; } = new();
    public string HuidigeHtml { get; set; } = "";
    public string OptimaleHtml { get; set; } = "";
}

// Lichtgewicht "wat staat er nu gepland"-weergave (#566) — zonder optimalisatie-berekening.
public class VeldbezettingItemDto
{
    public long? WedstrijdCode { get; set; }
    public string Wedstrijd { get; set; } = "";
    public string TeamNaam { get; set; } = "";
    public string? Uitteam { get; set; }
    public string? AanvangsTijd { get; set; }
    public string? Veld { get; set; }
    public string? Competitiesoort { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public int DuurMinuten { get; set; }
    public decimal Veldafmeting { get; set; }
}

public class AutoPlanToepassenRequestDto
{
    public string Datum { get; set; } = "";
    public int? BufferMinuten { get; set; }
}

public class AutoPlanToepassenResponseDto
{
    public int Bijgewerkt { get; set; }
    public int Mislukt { get; set; }
    public List<string> Fouten { get; set; } = new();
}

// ── Leermomenten (#323) ──

public class LeermomentDto
{
    public int Id { get; set; }
    public int OrigineleVerwerkingId { get; set; }
    public int CorrectionVerwerkingId { get; set; }
    public string OrigineelVerzoekType { get; set; } = "";
    public string? AfgeleidJuistType { get; set; }
    public string? OrigineleSamenvatting { get; set; }
    public string? CorrectieSamenvatting { get; set; }
    public bool IsGevalideerd { get; set; }
    public bool IsAfgewezen { get; set; }
    public DateTime MtaInserted { get; set; }
}

public class LeermomentenResponse
{
    public int Count { get; set; }
    public int Limit { get; set; }
    public List<LeermomentDto> Items { get; set; } = new();
}

public class LeermomentenStatsDto
{
    public int Pending { get; set; }
    public int Validated { get; set; }
    public int Rejected { get; set; }
}

// ── Teamaliassen (#701) ──

public class TeamAliasDto
{
    public int Id { get; set; }
    public string RuweTekst { get; set; } = "";
    public string RuweTekstGenormaliseerd { get; set; } = "";
    public int TeamId { get; set; }
    /// <summary>Canonieke teamnaam uit dbo.Teams; null als het team inmiddels is verwijderd.</summary>
    public string? Teamnaam { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public string Bron { get; set; } = "";
    public string Status { get; set; } = "";
    public int AantalKeerGebruikt { get; set; }
    /// <summary>UTC uit de database — altijd .ToLocalTime() vóór weergave.</summary>
    public DateTime? MtaInserted { get; set; }
    public DateTime? MtaModified { get; set; }
}

/// <summary>
/// Antwoord van <c>POST /api/beheer/teams/herstel</c> (#946). De voor- en na-tellingen staan er
/// bewust allebei in: "hersteld" zonder te laten zien wat er veranderde, is voor een beheerder niet
/// te onderscheiden van "er gebeurde niets".
/// </summary>
public class TeamHerstelDto
{
    public string ClubCode { get; set; } = string.Empty;
    public int TeamsVoor { get; set; }
    public int TeamsNa { get; set; }
    public int AliassenNa { get; set; }
    public int SleutelsGemigreerd { get; set; }
    public int DubbelenOpgeruimd { get; set; }
}

public class TeamAliassenResponse
{
    public int Count { get; set; }
    public int Limit { get; set; }
    public int Pending { get; set; }
    public int Validated { get; set; }
    public int Rejected { get; set; }
    public List<TeamAliasDto> Items { get; set; } = new();
}

// Thema (#325, #339)
public class ThemeDto
{
    public string Primary       { get; set; } = "#1b6ec2";
    public string Secondary     { get; set; } = "#6c757d";
    public string Accent        { get; set; } = "#0071c1";
    public string TextOnPrimary { get; set; } = "#ffffff";
    public string? ClubWebsiteUrl { get; set; }
    public string? FaviconUrl   { get; set; }
    public string? LogoUrl      { get; set; }
}

public class ThemeExtractResultDto
{
    public List<string> Colors    { get; set; } = new();
    public string?      FaviconUrl { get; set; }
    public string?      LogoUrl    { get; set; }
}

// Multi-club (#324)
public class ClubDto
{
    public string ClubCode { get; set; } = "";
    public string ClubName { get; set; } = "";
    public bool SyncEnabled { get; set; }
}

// Velden (#679: aanvulling met VeldType/HeeftKunstlicht/Actief voor Admin-CRUD)
public class VeldDto
{
    public int    VeldNummer      { get; set; }
    public string VeldNaam        { get; set; } = "";
    public string VeldType        { get; set; } = "kunstgras";
    public bool   HeeftKunstlicht { get; set; }
    public bool   Actief          { get; set; } = true;
}

// VeldBeschikbaarheid (#679: eerste GUI voor de al langer bestaande API)
public class VeldBeschikbaarheidDto
{
    public int    Id                   { get; set; }
    public int    VeldNummer           { get; set; }
    public string VeldNaam             { get; set; } = "";
    public int    DagVanWeek           { get; set; }
    public string BeschikbaarVanaf     { get; set; } = "";
    public string BeschikbaarTot       { get; set; } = "";
    public bool   GebruikZonsondergang { get; set; }
    // #581: NULL = standaardregime (geldt buiten elke actieve periode, zoals vóór deze feature).
    public int?    PeriodeId           { get; set; }
    public string? PeriodeNaam         { get; set; }
}

// VeldPeriode (#581: herbruikbaar regime, bijv. "Zomerstop" of "Competitie", met een geldigheidsrange)
public class VeldPeriodeDto
{
    public int    Id       { get; set; }
    public string Naam     { get; set; } = "";
    public string DatumVan { get; set; } = "";
    public string DatumTot { get; set; } = "";
    public bool   Actief   { get; set; } = true;
}

// VeldTraining (#679: trainingsschema per veld per weekdag)
public class VeldTrainingDto
{
    public int     Id            { get; set; }
    public int     VeldNummer    { get; set; }
    public string  VeldNaam      { get; set; } = "";
    public int     DagVanWeek    { get; set; }
    public string  VanTijd       { get; set; } = "";
    public string  TotTijd       { get; set; } = "";
    public string? Omschrijving  { get; set; }
    public bool    Actief        { get; set; } = true;
}

// Test data / ALLSTARS (#365)
public class AllstarsWedstrijdDto
{
    public string  BkMatches      { get; set; } = "";
    public string? Datum          { get; set; }
    public string? Aanvangstijd   { get; set; }
    public string? ThuisTeam      { get; set; }
    public string? UitTeam        { get; set; }
    public string? VeldNaam       { get; set; }
    public string? VeldSubpositie { get; set; }
    public string? Soort          { get; set; }
}

public class AllstarsVerplaatsDatumResultaat
{
    public bool Ok               { get; set; }
    public int  AantalVerplaatst { get; set; }
}
