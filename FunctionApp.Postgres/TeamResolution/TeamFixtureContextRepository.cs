using Planner.Endpoints.TeamResolution;
using Npgsql;
using Planner.Shared.TeamResolution;

namespace FunctionApp.Postgres.TeamResolution;

/// <summary>Strict club-scoped source ownership from programme teamnaam; no participation-based inference.</summary>
internal sealed class TeamFixtureContextRepository(string connectionString) : ITeamFixtureContextRepository
{
    public async Task<TeamFixtureContext> GetAsync(string clubCode, DateOnly date, IReadOnlyList<int> candidateIds)
    {
        if (candidateIds.Count != 2) throw new ArgumentException("Exactly two resolved identities required", nameof(candidateIds));
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
        return await TeamFixtureContextReader.ReadAsync(reader);
    }
}
