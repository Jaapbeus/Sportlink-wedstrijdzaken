using Planner.Shared;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Wire-contract-DTO's voor <c>POST /api/planner/check-availability</c>,
/// <c>/doordeweeks-beschikbaar</c> en <c>/herplan-check</c> — Postgres-tier-tegenhanger van de
/// gelijknamige typen in <c>FunctionApp/Planner/PlannerModels.cs</c> (issue 888 vervolg, §41).
/// <para>
/// Bewuste duplicatie, geen verwijzing naar de andere boom — zelfde motivatie als
/// <see cref="MatchModels"/>: twee volledig gescheiden implementatiebomen
/// (ARCHITECTUUR-DATABASE-TIERS.md §2), en pure DTO's zonder gedrag hebben geen "one location"-eis.
/// Veldnamen letterlijk gelijk aan het SQL Server-origineel zodat de JSON op beide tiers identiek is.
/// </para>
/// </summary>
public class CheckAvailabilityRequest
{
    public string Datum { get; set; } = string.Empty;
    public string? AanvangsTijd { get; set; }
    public string? Dagdeel { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public string? TeamNaam { get; set; }
    public string? Tegenstander { get; set; }
    public int? WedstrijdDuurMinuten { get; set; }
    public bool? HeelVeld { get; set; }
}

public class CheckAvailabilityResponse
{
    public bool Beschikbaar { get; set; }
    public SlotToewijzing? Toewijzing { get; set; }
    public TeamConflictInfo? TeamConflict { get; set; }
    public string? Reden { get; set; }
    public List<SlotToewijzing> Alternatieven { get; set; } = new();
    public List<BeschikbaarVenster>? BeschikbareVensters { get; set; }
    public List<string> Waarschuwingen { get; set; } = new();
}

public class TeamConflictInfo
{
    public string Wedstrijd { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public string EindTijd { get; set; } = string.Empty;
    public string VeldNaam { get; set; } = string.Empty;
}

public class BeschikbaarVenster
{
    public int VeldNummer { get; set; }
    public string VeldNaam { get; set; } = string.Empty;
    public string Van { get; set; } = string.Empty;
    public string Tot { get; set; } = string.Empty;
    public int MaxDuurMinuten { get; set; }
    public string? Opmerking { get; set; }
    public string? VeldType { get; set; }
}

public class DoordeweeksBeschikbaarRequest
{
    public string? DagFilter { get; set; }
    public int? DuurMinuten { get; set; }
    public string? LeeftijdsCategorie { get; set; }
}

public class DoordeweeksBeschikbaarResponse
{
    public List<DoordeweekseDatum> BeschikbareDatums { get; set; } = new();
    public string? DagFilter { get; set; }
    public string SeizoenEinde { get; set; } = string.Empty;
    public int AantalBeschikbaar { get; set; }
}

public class DoordeweekseDatum
{
    public string Datum { get; set; } = string.Empty;
    public string DagVanWeek { get; set; } = string.Empty;
    public string BeschikbaarVan { get; set; } = string.Empty;
    public string BeschikbaarTot { get; set; } = string.Empty;
    public string Zonsondergang { get; set; } = string.Empty;
    public int MaxDuurMinuten { get; set; }
    public bool PastGewensteDuur { get; set; }
    public List<BestaandeWedstrijdSamenvatting> GeplandeWedstrijden { get; set; } = new();
}

public class BestaandeWedstrijdSamenvatting
{
    public string Wedstrijd { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public string EindTijd { get; set; } = string.Empty;
}

public class HerplanCheckRequest
{
    public long Wedstrijdcode { get; set; }
    public string? VoorkeurTijd { get; set; }
    public string? Dagdeel { get; set; }
    public string? Richting { get; set; }
}

public class HerplanCheckResponse
{
    public ZoekWedstrijdResponse HuidigeWedstrijd { get; set; } = new();
    public bool Beschikbaar { get; set; }
    public List<SlotToewijzing> Alternatieven { get; set; } = new();
    public string? Reden { get; set; }
    public List<string> Waarschuwingen { get; set; } = new();
}
