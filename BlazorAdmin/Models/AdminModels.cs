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
