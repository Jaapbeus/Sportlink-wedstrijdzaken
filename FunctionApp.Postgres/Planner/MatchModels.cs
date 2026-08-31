namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Wire-contract-DTO's voor <c>POST /api/planner/bevestig</c>, <c>/zoek-wedstrijd</c> en
/// <c>/herplan-bevestig</c> — Postgres-tier-tegenhanger van de gelijknamige typen in
/// <c>FunctionApp/Planner/PlannerModels.cs</c> (#888).
/// <para>
/// Bewuste duplicatie, geen verwijzing naar de andere boom — zelfde motivatie als
/// <see cref="TeamScheduleWedstrijd"/>: twee volledig gescheiden implementatiebomen
/// (ARCHITECTUUR-DATABASE-TIERS.md §2), en pure DTO's zonder gedrag hebben geen "one location"-eis.
/// Veldnamen letterlijk gelijk aan het SQL Server-origineel zodat de JSON op beide tiers identiek is.
/// </para>
/// </summary>
public class BevestigRequest
{
    public string Datum { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public int VeldNummer { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public string? TeamNaam { get; set; }
    public string? Tegenstander { get; set; }
    public string? AangevraagdDoor { get; set; }
    public int? WedstrijdDuurMinuten { get; set; }
    public bool? HeelVeld { get; set; }
}

public class ZoekWedstrijdRequest
{
    public string TeamNaam { get; set; } = string.Empty;
    public string Datum { get; set; } = string.Empty;
}

public class ZoekWedstrijdResponse
{
    public long Wedstrijdcode { get; set; }
    public string Wedstrijd { get; set; } = string.Empty;
    public string Datum { get; set; } = string.Empty;
    public string AanvangsTijd { get; set; } = string.Empty;
    public string EindTijd { get; set; } = string.Empty;
    public string? VeldNaam { get; set; }
    public string? LeeftijdsCategorie { get; set; }
    public int DuurMinuten { get; set; }
    public decimal VeldDeelGebruik { get; set; }
}

public class HerplanBevestigRequest
{
    public long Wedstrijdcode { get; set; }
    public string GewensteAanvangsTijd { get; set; } = string.Empty;
    public int? GewenstVeldNummer { get; set; }
    public string? AangevraagdDoor { get; set; }
    public string? Opmerking { get; set; }
}

public class HerplanBevestigResponse
{
    public int Id { get; set; }
    public long Wedstrijdcode { get; set; }
    public string HuidigeWedstrijd { get; set; } = string.Empty;
    public string GewensteAanvangsTijd { get; set; } = string.Empty;
    public int? GewenstVeldNummer { get; set; }
    public string Status { get; set; } = string.Empty;
}
