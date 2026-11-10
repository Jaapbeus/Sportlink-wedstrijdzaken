using Microsoft.Data.SqlClient;
using Planner.Shared.TeamResolution;

namespace SportlinkFunction.TeamResolution;

/// <summary>Strict club-scoped source ownership from programme teamnaam; no participation-based inference.</summary>
internal sealed class TeamFixtureContextRepository(string connectionString) : ITeamFixtureContextRepository
{
    public async Task<TeamFixtureContext> GetAsync(string clubCode, DateOnly date, IReadOnlyList<int> candidateIds)
    {
        if (candidateIds.Count != 2) throw new ArgumentException("Exactly two resolved identities required", nameof(candidateIds));
        var fixtures = new List<TeamFixture>();
        var spellings = new List<TeamFixtureSpelling>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"SELECT [bk_matches], [teamnaam], [thuisteam], [uitteam], [aanvangstijd], [status]
                FROM [his].[matches] WHERE [ClubCode] = @club AND [mta_deleted] IS NULL AND TRY_CONVERT(date, [kaledatum]) = @date;
                SELECT [TeamId], [Teamnaam] FROM [dbo].[Teams]
                WHERE [ClubCode] = @club AND [IsActief] = 1 AND [TeamId] IN (@first, @second)
                UNION
                SELECT a.[TeamId], a.[RuweTekst] FROM [dbo].[TeamAliassen] a
                JOIN [dbo].[Teams] t ON t.[TeamId] = a.[TeamId] AND t.[ClubCode] = a.[ClubCode] AND t.[IsActief] = 1
                WHERE a.[ClubCode] = @club AND a.[Status] = 'validated' AND a.[TeamId] IN (@first, @second);", conn);
        cmd.Parameters.AddWithValue("club", clubCode);
        cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("first", candidateIds[0]);
        cmd.Parameters.AddWithValue("second", candidateIds[1]);
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
