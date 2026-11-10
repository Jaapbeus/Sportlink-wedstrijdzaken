using Npgsql;
using Planner.Shared.TeamResolution;

namespace FunctionApp.Postgres.TeamResolution;

/// <summary>Strict club-scoped source ownership from programme teamnaam; no participation-based inference.</summary>
internal sealed class TeamFixtureContextRepository(string connectionString) : ITeamFixtureContextRepository
{
    public async Task<TeamFixtureContext> GetAsync(string clubCode, DateOnly date, IReadOnlyList<int> candidateIds)
    {
        if (candidateIds.Count != 2) throw new ArgumentException("Exactly two resolved identities required", nameof(candidateIds));
        var fixtures = new List<TeamFixture>();
        var spellings = new List<TeamFixtureSpelling>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"SELECT bk_matches, teamnaam, thuisteam, uitteam, aanvangstijd, status
                FROM his.matches WHERE clubcode = @club AND mta_deleted IS NULL AND kaledatum::date = @date;
                SELECT teamid, teamnaam FROM public.teams
                WHERE clubcode = @club AND isactief = TRUE AND teamid = ANY(@ids)
                UNION
                SELECT a.teamid, a.ruwetekst FROM public.teamaliassen a
                JOIN public.teams t ON t.teamid = a.teamid AND t.clubcode = a.clubcode AND t.isactief = TRUE
                WHERE a.clubcode = @club AND a.status = 'validated' AND a.teamid = ANY(@ids);", conn);
        cmd.Parameters.AddWithValue("club", clubCode);
        cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("ids", candidateIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var status = reader.IsDBNull(5) ? "" : reader.GetString(5);
            if (string.Equals(status.Trim(), "AFGELAST", StringComparison.OrdinalIgnoreCase)) continue;
            if (Enumerable.Range(0, 4).Any(reader.IsDBNull)) continue;
            fixtures.Add(new TeamFixture(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        await reader.NextResultAsync();
        while (await reader.ReadAsync())
            spellings.Add(new TeamFixtureSpelling(reader.GetInt32(0), reader.GetString(1)));
        return new TeamFixtureContext(fixtures, spellings);
    }
}
