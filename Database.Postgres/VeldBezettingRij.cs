namespace Database.Postgres;

/// <summary>
/// Postgres-tier-tegenhanger van FunctionApp's <c>BestaandeWedstrijd</c> (#819) — een geresolveerde
/// rij uit <see cref="PostgresPlannerViewGenerator"/>, ná veldresolutie. Bewust een eigen, lokaal
/// type in plaats van hergebruik van FunctionApp's <c>BestaandeWedstrijd</c>: <c>Database.Postgres</c>
/// wordt niet door FunctionApp gerefereerd op een manier die de omgekeerde afhankelijkheid toestaat
/// (zie docs/ARCHITECTUUR-DATABASE-TIERS.md §2, "volledig gescheiden, parallelle implementatiebomen").
/// </summary>
public sealed record VeldBezettingRij(
    DateOnly Datum,
    TimeOnly AanvangsTijd,
    TimeOnly EindTijd,
    int VeldNummer,
    string? VeldSubpositie,
    decimal VeldDeelGebruik,
    string? LeeftijdsCategorie,
    string? TeamNaam,
    string? Wedstrijd,
    string Bron,
    string ClubCode,
    long? Wedstrijdcode);
