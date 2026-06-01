using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Planner;

/// <summary>
/// Repository voor dbo.TeamRegels data-access.
/// Extracted uit PlannerDataAccess (#474).
/// </summary>
internal static class TeamRulesRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    internal static async Task<List<TeamRegel>> GetTeamRulesAsync(string teamNaam, string? clubCode = null)
    {
        var cc = clubCode ?? SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";
        var results = new List<TeamRegel>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [TeamNaam], [RegelType], [WaardeMinuten], [WaardeVeldNummer], [WaardeTijd], [Prioriteit]
            FROM [dbo].[TeamRegels]
            WHERE [TeamNaam] = @team AND [Actief] = 1 AND [ClubCode] = @cc
            ORDER BY [Prioriteit] DESC
        ", conn);
        cmd.Parameters.AddWithValue("@team", teamNaam);
        cmd.Parameters.AddWithValue("@cc", cc);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new TeamRegel
            {
                TeamNaam = reader.GetString(0),
                RegelType = reader.GetString(1),
                WaardeMinuten = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                WaardeVeldNummer = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                WaardeTijd = reader.IsDBNull(4) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
                Prioriteit = reader.GetInt32(5)
            });
        }
        return results;
    }

    internal static async Task<Dictionary<string, (int bufferVoor, int bufferNa)>> GetAllTeamBuffersAsync(string? clubCode = null)
    {
        var cc = clubCode ?? SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";
        var result = new Dictionary<string, (int bufferVoor, int bufferNa)>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [TeamNaam], [RegelType], [WaardeMinuten]
            FROM [dbo].[TeamRegels]
            WHERE [RegelType] IN ('BufferVoor', 'BufferNa') AND [Actief] = 1 AND [WaardeMinuten] IS NOT NULL
              AND [ClubCode] = @cc
        ", conn);
        cmd.Parameters.AddWithValue("@cc", cc);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var team   = reader.GetString(0);
            var type   = reader.GetString(1);
            var min    = reader.GetInt32(2);
            if (!result.ContainsKey(team)) result[team] = (0, 0);
            var cur = result[team];
            result[team] = type == "BufferVoor"
                ? (Math.Max(cur.bufferVoor, min), cur.bufferNa)
                : (cur.bufferVoor, Math.Max(cur.bufferNa, min));
        }
        return result;
    }
}
