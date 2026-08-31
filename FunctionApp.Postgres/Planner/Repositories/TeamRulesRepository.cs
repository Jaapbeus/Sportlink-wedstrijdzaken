using Npgsql;
using Planner.Shared;

namespace FunctionApp.Postgres.Planner.Repositories;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Planner/Repositories/TeamRulesRepository.cs</c>
/// (#888) — <c>public.teamregels</c> data-access voor de scheduling-engine (§38).
///
/// <para>
/// <b>IN-clause vertaald naar <c>= ANY(@teams)</c>.</b> Het origineel bouwt voor
/// <see cref="GetTeamRulesForTeamsAsync"/> per team een eigen genummerde parameter
/// (<c>@team0</c>, <c>@team1</c>, ...) omdat T-SQL geen array-parameter kent. Npgsql ondersteunt
/// wél rechtstreekse array-binding (<c>text[]</c>), dus die omweg is hier niet nodig — functioneel
/// gelijk, minder code.
/// </para>
/// </summary>
internal static class TeamRulesRepository
{
    internal static async Task<List<TeamRegel>> GetTeamRulesAsync(
        string connectionString, string teamNaam, string? clubCode = null)
    {
        var cc = PostgresClubScope.Resolve(clubCode);
        var results = new List<TeamRegel>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT teamnaam, regeltype, waardeminuten, waardeveldnummer, waardetijd, prioriteit
            FROM public.teamregels
            WHERE teamnaam = @team AND actief = true AND clubcode = @clubcode
            ORDER BY prioriteit
            """, conn);
        cmd.Parameters.AddWithValue("team", teamNaam);
        cmd.Parameters.AddWithValue("clubcode", cc);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(LeesRegel(reader));
        return results;
    }

    /// <summary>
    /// Haalt de regels voor meerdere teams op in één query (#575) — vervangt een N+1-lus in
    /// AvailabilityService/RescheduleService. Teams zonder regels komen terug als lege lijst.
    /// </summary>
    internal static async Task<Dictionary<string, List<TeamRegel>>> GetTeamRulesForTeamsAsync(
        string connectionString, IEnumerable<string> teamNamen, string? clubCode = null)
    {
        var teams = teamNamen
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new Dictionary<string, List<TeamRegel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in teams) results[team] = new List<TeamRegel>();
        if (teams.Count == 0) return results;

        var cc = PostgresClubScope.Resolve(clubCode);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT teamnaam, regeltype, waardeminuten, waardeveldnummer, waardetijd, prioriteit
            FROM public.teamregels
            WHERE teamnaam = ANY(@teams) AND actief = true AND clubcode = @clubcode
            ORDER BY teamnaam, prioriteit
            """, conn);
        cmd.Parameters.AddWithValue("teams", teams.ToArray());
        cmd.Parameters.AddWithValue("clubcode", cc);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var regel = LeesRegel(reader);
            // Sleutel uit de aanvraag aanhouden — DB-casing kan afwijken van de teamnaam in de
            // bezetting, en callers zoeken op hun eigen sleutel.
            if (!results.TryGetValue(regel.TeamNaam, out var list))
            {
                list = new List<TeamRegel>();
                results[regel.TeamNaam] = list;
            }
            list.Add(regel);
        }
        return results;
    }

    /// <summary>
    /// Voorkeursveld-regels per team (#666). Prioriteit: laag getal = belangrijker. Bij meerdere
    /// 'VoorkeurVeld'-regels voor hetzelfde team wint de rij met de laagste prioriteit.
    /// </summary>
    internal static async Task<Dictionary<string, TeamVoorkeurVeld>> GetAllTeamVoorkeurVeldenAsync(
        string connectionString, string? clubCode = null)
    {
        var cc = PostgresClubScope.Resolve(clubCode);
        var result = new Dictionary<string, TeamVoorkeurVeld>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT teamnaam, waardeveldnummer, waardetijd, prioriteit
            FROM public.teamregels
            WHERE regeltype = 'VoorkeurVeld' AND actief = true AND waardeveldnummer IS NOT NULL
              AND clubcode = @clubcode
            ORDER BY teamnaam, prioriteit
            """, conn);
        cmd.Parameters.AddWithValue("clubcode", cc);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var team = reader.GetString(0);
            // Eerste rij per team wint: de query sorteert op prioriteit oplopend.
            if (result.ContainsKey(team)) continue;
            result[team] = new TeamVoorkeurVeld
            {
                TeamNaam = team,
                VeldNummer = reader.GetInt32(1),
                Tijd = reader.IsDBNull(2) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(2)),
                Prioriteit = reader.GetInt32(3)
            };
        }
        return result;
    }

    internal static async Task<Dictionary<string, (int bufferVoor, int bufferNa)>> GetAllTeamBuffersAsync(
        string connectionString, string? clubCode = null)
    {
        var cc = PostgresClubScope.Resolve(clubCode);
        var result = new Dictionary<string, (int bufferVoor, int bufferNa)>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT teamnaam, regeltype, waardeminuten
            FROM public.teamregels
            WHERE regeltype IN ('BufferVoor', 'BufferNa') AND actief = true AND waardeminuten IS NOT NULL
              AND clubcode = @clubcode
            """, conn);
        cmd.Parameters.AddWithValue("clubcode", cc);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var team = reader.GetString(0);
            var type = reader.GetString(1);
            var min = reader.GetInt32(2);
            if (!result.ContainsKey(team)) result[team] = (0, 0);
            var cur = result[team];
            result[team] = type == "BufferVoor"
                ? (Math.Max(cur.bufferVoor, min), cur.bufferNa)
                : (cur.bufferVoor, Math.Max(cur.bufferNa, min));
        }
        return result;
    }

    private static TeamRegel LeesRegel(NpgsqlDataReader reader) => new()
    {
        TeamNaam = reader.GetString(0),
        RegelType = reader.GetString(1),
        WaardeMinuten = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        WaardeVeldNummer = reader.IsDBNull(3) ? null : reader.GetInt32(3),
        WaardeTijd = reader.IsDBNull(4) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
        Prioriteit = reader.GetInt32(5)
    };
}
