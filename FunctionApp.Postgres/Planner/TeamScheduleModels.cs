namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Antwoordmodellen voor <c>GET /api/planner/team-schedule</c> — Postgres-tier-tegenhanger van de
/// gelijknamige typen in <c>FunctionApp/Planner/PlannerModels.cs</c> (#888).
/// <para>
/// Bewuste duplicatie, geen verwijzing naar de andere boom: de twee tiers zijn volledig gescheiden
/// implementatiebomen (ARCHITECTUUR-DATABASE-TIERS.md §2). Het gaat hier bovendien om pure
/// DTO's zonder gedrag — er zit geen logica in die uit de pas kan gaan lopen zonder dat een
/// assertie dat merkt. De veldnamen zijn letterlijk gelijk gehouden zodat de JSON die de Admin GUI
/// binnenkrijgt op beide tiers identiek is.
/// </para>
/// </summary>
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

/// <inheritdoc cref="TeamScheduleWedstrijd"/>
public class TeamScheduleZaterdag
{
    public string Datum { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;    // "vrij" | "oefenwedstrijd" | "bezet"
    public TeamScheduleWedstrijd? BezetDoor { get; set; }
}

/// <inheritdoc cref="TeamScheduleWedstrijd"/>
public class TeamScheduleResponse
{
    public string Team { get; set; } = string.Empty;
    public string SeizoenEinde { get; set; } = string.Empty;
    public List<TeamScheduleZaterdag> Zaterdagen { get; set; } = new();
    public List<TeamScheduleWedstrijd> Wedstrijden { get; set; } = new();
}
