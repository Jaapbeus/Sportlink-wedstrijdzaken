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
        var cc = SystemUtilities.AppSettings.RequireClubCode(clubCode);
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

    /// <summary>
    /// Haalt de regels voor meerdere teams op in één query (#575).
    /// Vervangt de N+1-lus in AvailabilityService/RescheduleService: op een drukke
    /// zaterdag scheelt dat tientallen roundtrips naar de SQL free tier.
    /// Teams zonder regels komen terug als lege lijst, zodat callers geen null-check nodig hebben.
    /// </summary>
    internal static async Task<Dictionary<string, List<TeamRegel>>> GetTeamRulesForTeamsAsync(
        IEnumerable<string> teamNamen, string? clubCode = null)
    {
        var teams = teamNamen
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new Dictionary<string, List<TeamRegel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in teams) results[team] = new List<TeamRegel>();
        if (teams.Count == 0) return results;

        var cc = SystemUtilities.AppSettings.RequireClubCode(clubCode);
        var paramNames = teams.Select((_, i) => $"@team{i}").ToList();

        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT [TeamNaam], [RegelType], [WaardeMinuten], [WaardeVeldNummer], [WaardeTijd], [Prioriteit]
            FROM [dbo].[TeamRegels]
            WHERE [TeamNaam] IN ({string.Join(", ", paramNames)})
              AND [Actief] = 1 AND [ClubCode] = @cc
            ORDER BY [TeamNaam], [Prioriteit] DESC
        ", conn);
        for (int i = 0; i < teams.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], teams[i]);
        cmd.Parameters.AddWithValue("@cc", cc);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var regel = new TeamRegel
            {
                TeamNaam = reader.GetString(0),
                RegelType = reader.GetString(1),
                WaardeMinuten = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                WaardeVeldNummer = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                WaardeTijd = reader.IsDBNull(4) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
                Prioriteit = reader.GetInt32(5)
            };
            // Sleutel uit de aanvraag aanhouden — DB-casing kan afwijken van de teamnaam
            // in de bezetting, en callers zoeken op hun eigen sleutel.
            if (!results.TryGetValue(regel.TeamNaam, out var list))
            {
                list = new List<TeamRegel>();
                results[regel.TeamNaam] = list;
            }
            list.Add(regel);
        }
        return results;
    }

    internal static async Task<Dictionary<string, (int bufferVoor, int bufferNa)>> GetAllTeamBuffersAsync(string? clubCode = null)
    {
        var cc = SystemUtilities.AppSettings.RequireClubCode(clubCode);
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
