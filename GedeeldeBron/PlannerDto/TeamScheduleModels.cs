using Planner.Shared;
using Planner.Shared.Deel;
using Planner.Shared.Email;

// Gedeelde bron voor de planner-wire-DTO's (#1856): één bestand, door beide databasetiers
// link-gecompileerd (zie docs/ARCHITECTUUR-CODEKWALITEIT.md, regel 2b). Wijzig je iets hier, dan
// wijzigt het op SQL Server én Postgres tegelijk; de naamruimte per tier blijft die van de tier.
// Endpoints: team-schedule.
#if PLANNER_DTO_POSTGRES
namespace FunctionApp.Postgres.Planner;
#else
namespace SportlinkFunction.Planner;
#endif

// ── Team schedule modellen (#70) ──

public class TeamScheduleWedstrijd
{
    public string Datum { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public string ThuisUit { get; set; } = string.Empty;  // "thuis" | "uit"
    public string Tegenstander { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;      // "competitie" | "beker" | "oefenwedstrijd"
    public string? Veld { get; set; }
    public long? Wedstrijdcode { get; set; }
}

public class TeamScheduleZaterdag
{
    public string Datum { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;    // "vrij" | "oefenwedstrijd" | "bezet"
    public TeamScheduleWedstrijd? BezetDoor { get; set; }
}

public class TeamScheduleResponse
{
    public string Team { get; set; } = string.Empty;
    public string SeizoenEinde { get; set; } = string.Empty;
    public List<TeamScheduleZaterdag> Zaterdagen { get; set; } = new();
    public List<TeamScheduleWedstrijd> Wedstrijden { get; set; } = new();
}
