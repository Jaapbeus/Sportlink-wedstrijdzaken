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
    public bool SportlinkDryRun { get; set; } = true;
    /// <summary>#1437: clubinstelling Spelactiviteit voor nieuwe oefenwedstrijden (omschrijving of IdTag uit Sportlinks lijst).</summary>
    public string? SportlinkSpelactiviteit { get; set; }
    /// <summary>#1459: PDF-export (QuestPDF Community) per club; standaard uit tot een beheerder de licentievoorwaarden bevestigt.</summary>
    public bool PdfExportIngeschakeld { get; set; }
    /// <summary>#1568: zekerheidspoort — onzekere antwoorden gaan ter review i.p.v. naar de afzender. Standaard aan.</summary>
    public bool ZekerheidspoortActief { get; set; } = true;
}

/// <summary>#1459: antwoord van <c>GET api/planner/pdf-export</c> (voor elke ingelogde rol).</summary>
public class PdfExportStatusDto
{
    public bool PdfExportIngeschakeld { get; set; }
}

public class GeocodeResultDto
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string DisplayName { get; set; } = "";
}

/// <summary>#1647: spiegelt de respons van <c>GET /api/planner/menu-toegang</c> — de menu-sleutels
/// (<c>menu.*</c>) die de ingelogde gebruiker volgens "Rechten per rol" mag zien.</summary>
public class MenuToegangDto
{
    public List<string> FeatureKeys { get; set; } = new();
    /// <summary>Niet-gevoelige menuvlag: staat de Sportlink Web Extension aan voor de club? Komt hier vandaan
    /// omdat de instellingenroute admin-only is en een niet-admin-rol de vlag anders nooit zou krijgen.</summary>
    public bool SportlinkExtensieActief { get; set; }
}

/// <summary>#1341: één FeatureKey met zijn huidige aan/uit-stand voor de Wedstrijdzaken-rol bij
/// deze club — spiegelt de anonieme respons van <c>GET /api/beheer/rolfeatureinstellingen</c>.</summary>
public class RolFeatureInstellingDto
{
    public string RolNaam { get; set; } = "";
    public string FeatureKey { get; set; } = "";
    public bool Enabled { get; set; }
}

public class SettingsUpdateDto
{
    // #1003: GewijzigdDoor bewust verwijderd — de API bepaalt de audit-actor uitsluitend
    // server-side uit gevalideerde Easy Auth-claims, nooit uit client-input.
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

    /// <summary>Meest recente (of opgevraagde) sync-job — null zolang er nog nooit een job is gestart (#1138).</summary>
    public SyncJobDto? Job { get; set; }
    public SyncJobDto? ActiveJob { get; set; }
    public SyncJobDto? LastCompletedJob { get; set; }
    public List<SyncJobDto> Jobs { get; set; } = new();
    public bool LogAvailable { get; set; } = true;
}

/// <summary>Status van een sync-job op de "sync-jobs"-queue (#1138). Status: pending/running/succeeded/failed.</summary>
public class SyncJobDto
{
    public Guid Id { get; set; }
    public string? Status { get; set; }
    public int WeekOffsetFrom { get; set; }
    public int WeekOffsetTo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int Attempt { get; set; }
    public List<SyncLogEntryDto> Phases { get; set; } = new();
}

public class SyncLogEntryDto
{
    public Guid JobId { get; set; }
    public int Attempt { get; set; }
    public string? Phase { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Status { get; set; }
    public int? Fetched { get; set; }
    public int? Added { get; set; }
    public int? Updated { get; set; }
    public int? Failed { get; set; }
    public int? Progress { get; set; }
    public int? Total { get; set; }
    public string? ReasonCode { get; set; }
}

public class TriggerSyncResultDto
{
    public string? Status { get; set; }
    public Guid? JobId { get; set; }
    public int WeekOffsetFrom { get; set; }
    public int WeekOffsetTo { get; set; }
    public DateTime Tijdstip { get; set; }
    public string? Melding { get; set; }
    public bool AlreadyRunning { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? Phase { get; set; }
}

// Multi-club (#324)
public class ClubDto
{
    public string ClubCode { get; set; } = "";
    public string ClubName { get; set; } = "";
    public bool SyncEnabled { get; set; }
}
