using Planner.Shared;
using Planner.Shared.Deel;
using Planner.Shared.Email;

// Gedeelde bron voor de planner-wire-DTO's (#1856): één bestand, door beide databasetiers
// link-gecompileerd (zie docs/ARCHITECTUUR-CODEKWALITEIT.md, regel 2b). Wijzig je iets hier, dan
// wijzigt het op SQL Server én Postgres tegelijk; de naamruimte per tier blijft die van de tier.
// Endpoints: auto-plan en auto-plan/toepassen.
#if PLANNER_DTO_POSTGRES
namespace FunctionApp.Postgres.Planner;
#else
namespace SportlinkFunction.Planner;
#endif

public class AutoPlanRequest
{
    public string Datum { get; set; } = string.Empty;
    public int? BufferMinuten { get; set; }
}

// AutoPlanWedstrijdItem en AutoPlanToepassenRequest staan (nog) per tier: FunctionApp/Planner/PlannerModels.cs
// en FunctionApp.Postgres/Planner/AutoPlanModels.cs, tot de open PR's die ze wijzigen gemerged zijn.

public class AutoPlanResponse
{
    public string Datum { get; set; } = string.Empty;
    public int TotaalWedstrijden { get; set; }
    public int ZonderVeld { get; set; }
    public int ZonderTijd { get; set; }
    public int TeWijzigen { get; set; }
    public int NietInplanbaar { get; set; }
    public string? GeschatteEindTijd { get; set; }
    public List<AutoPlanWedstrijdItem> Wedstrijden { get; set; } = new();
    public string HuidigeHtml { get; set; } = string.Empty;
    public string OptimaleHtml { get; set; } = string.Empty;
}

public class AutoPlanToepassenResponse
{
    public int Bijgewerkt { get; set; }
    public int Mislukt { get; set; }
    public List<string> Fouten { get; set; } = new();
}
