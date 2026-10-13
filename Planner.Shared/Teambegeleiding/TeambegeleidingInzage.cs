using System.Data.Common;

namespace Planner.Shared.Teambegeleiding;

// Inzagelog voor de contactgegevens van teambegeleiding (#1701), gedeeld tussen beide database-tiers.
//
// Naar het model van het feedback-inzagelog (#764, docs/FEEDBACK.md §4): wie (Entra object-ID als
// pseudoniem + weergavenaam als momentopname, uitsluitend uit het Easy Auth-principal), wanneer (UTC),
// welk team en hoeveel rijen. Nooit gegevens van de ingeziene personen: geen naam, e-mailadres,
// telefoonnummer of functie. Dat is hier geen afspraak maar een eigenschap van het type: het record
// heeft simpelweg geen veld waarin zulke gegevens passen.
//
// Geen runtime-providerswitch (regel 2 van de multi-tier-strategie): elke tier levert een eigen
// ITeambegeleidingInzageStore (SQL Server: FunctionApp/Admin/SqlTeambegeleidingInzageStore,
// Postgres: FunctionApp.Postgres/Admin/PostgresTeambegeleidingInzageStore) aan de gedeelde
// orkestratie in Planner.Endpoints/Teambegeleiding/TeambegeleidingInzageEndpointCore.

/// <summary>Bewaartermijn van het inzagelog (#1701) — één plek, door beide tiers gebruikt.</summary>
public static class TeambegeleidingInzageRetentie
{
    /// <summary>
    /// Inzageregels worden na dit aantal maanden verwijderd. Gelijk aan de bewaartermijn van de
    /// teambegeleiding zelf en van het importlogboek (1 jaar): een inzageregel heeft geen doel meer als
    /// de ingeziene gegevens zelf al verdwenen zijn. Verandert de eigenaar de termijn, dan is dat deze
    /// constante plus docs/VERZOEK-VAN-BETROKKENE.md en SECURITY.md.
    /// </summary>
    public const int InzageLogMaanden = 12;

    /// <summary>Inzageregels van vóór dit tijdstip zijn verlopen.</summary>
    public static DateTime VerwijderVoor(DateTime nuUtc) => nuUtc.AddMonths(-InzageLogMaanden);
}

/// <summary>De ingelogde gebruiker die de contactgegevens opvraagt, uit het Easy Auth-principal (nooit uit de request).</summary>
public sealed record TeambegeleidingInzager(string? ObjectId, string? Naam);

/// <summary>
/// Eén contactkaart zoals <c>GET /api/beheer/teambegeleiding/{team}</c> die teruggeeft. De eigenschapsnamen
/// zijn het API-contract (camelCase in JSON) en gelijk aan wat de endpoints vóór #1701 als anoniem object gaven.
/// </summary>
public sealed record TeambegeleiderContact(string Naam, string Teamrol, string? Emailadres, string? Telefoonnummer, string? Functie)
{
    /// <summary>
    /// Leest één rij met de kolommen in deze volgorde: naam, teamrol, e-mailadres, telefoonnummer, functie.
    /// Gedeeld door beide tiers (SqlDataReader en NpgsqlDataReader zijn allebei een <see cref="DbDataReader"/>).
    /// </summary>
    public static TeambegeleiderContact LeesVan(DbDataReader reader) => new(
        reader.IsDBNull(0) ? "" : reader.GetString(0),
        reader.IsDBNull(1) ? "" : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4));
}

/// <summary>
/// Eén inzageregel. Bevat bewust geen enkel gegeven van de ingeziene begeleiders — alleen dát er voor een
/// team is gekeken, door wie, en hoeveel kaarten dat opleverde.
/// </summary>
public sealed record TeambegeleidingInzageNieuw(
    string ClubCode, string? InzienDoorObjectId, string? InzienDoorNaam, string Team, int AantalRijen)
{
    /// <summary>Kolomlengtes van <c>avg.TeambegeleidingInzageLog</c> (beide tiers gelijk).</summary>
    public const int MaxObjectId = 64;
    public const int MaxNaam = 200;
    public const int MaxTeam = 100;

    /// <summary>
    /// Stelt de regel samen en kort vrije waarden in tot de kolomlengte: de teamnaam komt uit de route
    /// (gebruikersinvoer) en mag de opvraging niet laten mislukken op een te lange waarde.
    /// </summary>
    public static TeambegeleidingInzageNieuw Maak(string clubCode, TeambegeleidingInzager wie, string team, int aantalRijen) => new(
        clubCode,
        Kort(wie.ObjectId, MaxObjectId),
        Kort(wie.Naam, MaxNaam),
        Kort(team, MaxTeam) ?? "",
        aantalRijen);

    private static string? Kort(string? waarde, int max) =>
        waarde is null ? null : waarde.Length <= max ? waarde : waarde[..max];
}

/// <summary>Opslagcontract voor het ophalen van contactgegevens en het vastleggen van de inzage.</summary>
public interface ITeambegeleidingInzageStore
{
    /// <summary>De begeleiders van één team van de club: trainer vóór coach vóór teamleider vóór de rest, daarna op naam.</summary>
    Task<IReadOnlyList<TeambegeleiderContact>> HaalContactgegevensAsync(string clubCode, string team);

    /// <summary>Legt één inzage vast. Een fout hier moet de opvraging laten mislukken (geen gegevens zonder log).</summary>
    Task LogInzageAsync(TeambegeleidingInzageNieuw inzage);
}
