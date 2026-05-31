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
}

public class GeocodeResultDto
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string DisplayName { get; set; } = "";
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
}

// ── Speeltijden ──

public class SpeeltijdDto
{
    public string Leeftijd { get; set; } = "";
    public decimal Veldafmeting { get; set; } = 1.00m;
    public int WedstrijdTotaal { get; set; }
    public int WedstrijdHelft { get; set; }
    public int WedstrijdRust { get; set; }
}

// ── Dagplanning / Optimaliseer ──

public class OptimaliseerRequestDto
{
    public string Datum { get; set; } = "";
    public string? Doel { get; set; }
    public string? GewensteEindtijd { get; set; }
    public int? BufferMinuten { get; set; }
}

public class OptimaliseerResponseDto
{
    public string Datum { get; set; } = "";
    public string HuidigeEindtijd { get; set; } = "";
    public string? GeschatteNieuweEindtijd { get; set; }
    public int AantalVerplaatsingen { get; set; }
    public int AantalVanGrasveldVerplaatst { get; set; }
    public List<OptimalisatieSuggestieDto> Suggesties { get; set; } = new();
    public string HtmlPlanner { get; set; } = "";
    public bool VoldoendeRuimte { get; set; }
    public string? VoldoendeRuimteMelding { get; set; }
    public VeldCapaciteitDto? CapaciteitOverzicht { get; set; }
}

public class OptimalisatieSuggestieDto
{
    public string Wedstrijd { get; set; } = "";
    public int HuidigVeldNummer { get; set; }
    public string HuidigVeld { get; set; } = "";
    public string HuidigeTijd { get; set; } = "";
    public int NieuwVeldNummer { get; set; }
    public string NieuwVeld { get; set; } = "";
    public string NieuweTijd { get; set; } = "";
    public string Reden { get; set; } = "";
}

public class VeldCapaciteitDto
{
    public int TotaalBeschikbareMinuten { get; set; }
    public int TotaalBezettMinuten { get; set; }
    public double BezettingsPercentage { get; set; }
    public int AantalWedstrijdenOpGrasveld { get; set; }
    public int AantalLegeVelden { get; set; }
}

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
    public string Status { get; set; } = "ongewijzigd";
    public string? NietInplanbaaarReden { get; set; }
    // Voorkeurstijd (null = geen voorkeur geconfigureerd voor dit team)
    public string? VoorkeurTijd { get; set; }
    public int? VoorkeurAfwijkingMinuten { get; set; }
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
