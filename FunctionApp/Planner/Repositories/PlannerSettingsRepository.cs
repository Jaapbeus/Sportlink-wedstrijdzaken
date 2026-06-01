using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Planner;

/// <summary>
/// Repository voor opzoektabellen: Speeltijden, Velden, Zonsondergang, Seizoen, VoorkeurTijden.
/// Extracted uit PlannerDataAccess (#474).
/// </summary>
internal static class PlannerSettingsRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    internal static async Task<Speeltijd?> GetSpeeltijdAsync(string leeftijdsCategorie, string? clubCode = null)
    {
        // Normaliseer eerst zodat "JO15 Meiden" → "MO15" (#486)
        leeftijdsCategorie = LeeftijdNormalisatie.Normaliseer(leeftijdsCategorie);
        var cc = clubCode ?? SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT [Leeftijd], [Veldafmeting], [WedstrijdTotaal] FROM [dbo].[Speeltijden] WHERE [Leeftijd] = @cat AND [ClubCode] = @cc", conn);
        cmd.Parameters.AddWithValue("@cat", leeftijdsCategorie);
        cmd.Parameters.AddWithValue("@cc", cc);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return new Speeltijd { Leeftijd = reader.GetString(0), Veldafmeting = reader.GetDecimal(1), WedstrijdTotaal = reader.GetInt32(2) };
        return null;
    }

    internal static async Task<List<VeldInfo>> GetVeldenAsync(string? clubCode = null)
    {
        clubCode ??= SystemUtilities.AppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");
        var results = new List<VeldInfo>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT [VeldNummer], [VeldNaam], ISNULL([VeldType], 'kunstgras') AS [VeldType], [HeeftKunstlicht] FROM [dbo].[Velden] WHERE [Actief] = 1 AND [ClubCode] = @clubCode ORDER BY [VeldNummer]", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new VeldInfo { VeldNummer = reader.GetInt32(0), VeldNaam = reader.GetString(1), VeldType = reader.GetString(2), HeeftKunstlicht = reader.GetBoolean(3) });
        return results;
    }

    internal static async Task<Dictionary<string, int>> GetVeldenLookupAsync()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("SELECT [VeldNaam], [VeldNummer] FROM [dbo].[Velden] WHERE [Actief] = 1", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0).TrimEnd()] = reader.GetInt32(1);
        return result;
    }

    internal static async Task<Dictionary<string, Speeltijd>> GetSpeeltijdenLookupAsync(string? clubCode = null)
    {
        var cc = clubCode ?? SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";
        var result = new Dictionary<string, Speeltijd>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT [Leeftijd], [Veldafmeting], [WedstrijdTotaal] FROM [dbo].[Speeltijden] WHERE [ClubCode] = @cc", conn);
        cmd.Parameters.AddWithValue("@cc", cc);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = new Speeltijd { Leeftijd = reader.GetString(0), Veldafmeting = reader.GetDecimal(1), WedstrijdTotaal = reader.GetInt32(2) };
        return result;
    }

    internal static async Task<Dictionary<string, string>> GetTeamLeeftijdLookupAsync(string clubCode)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT [teamnaam],
                   {LeeftijdNormalisatie.SqlExpr("[leeftijdscategorie]")}
            FROM [his].[teams]
            WHERE [leeftijdscategorie] IS NOT NULL AND [leeftijdscategorie] <> ''
              AND [ClubCode] = @clubCode", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    internal static async Task<TimeOnly?> GetSunsetAsync(DateOnly date)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT [Zonsondergang] FROM [dbo].[Zonsondergang] WHERE [Datum] = @date", conn);
        cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        var result = await cmd.ExecuteScalarAsync();
        if (result is TimeSpan ts) return TimeOnly.FromTimeSpan(ts);
        return null;
    }

    internal static async Task PopulateSunsetTableAsync(DateOnly from, DateOnly to)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var sunset = SunsetCalculator.GetSunset(date);
            using var cmd = new SqlCommand(@"
                IF NOT EXISTS (SELECT 1 FROM [dbo].[Zonsondergang] WHERE [Datum] = @date)
                    INSERT INTO [dbo].[Zonsondergang] ([Datum], [Zonsondergang]) VALUES (@date, @sunset)
                ELSE
                    UPDATE [dbo].[Zonsondergang] SET [Zonsondergang] = @sunset WHERE [Datum] = @date
            ", conn);
            cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("@sunset", sunset.ToTimeSpan());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    internal static async Task<DateOnly?> GetSeasonEndDateAsync()
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("SELECT MAX(DateUntil) FROM [dbo].[Season]", conn);
        var result = await cmd.ExecuteScalarAsync();
        if (result != null && result != DBNull.Value)
            return DateOnly.FromDateTime(Convert.ToDateTime(result));
        return null;
    }

    internal static async Task<DateTime?> GetLastSyncTimestampAsync()
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("SELECT [LastSyncTimestamp] FROM [dbo].[AppSettings]", conn);
        var result = await cmd.ExecuteScalarAsync();
        if (result != null && result != DBNull.Value)
            return Convert.ToDateTime(result);
        return null;
    }

    internal static async Task<Dictionary<string, List<(TimeOnly Tijd, int Prioriteit)>>> GetVoorkeurTijdenLookupAsync(int dagVanWeek, string clubCode)
    {
        var result = new Dictionary<string, List<(TimeOnly, int)>>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [TeamNaam], [VoorkeurTijd], [Prioriteit]
            FROM [dbo].[TeamVoorkeurTijden]
            WHERE [DagVanWeek] = @dag AND [Actief] = 1 AND [ClubCode] = @clubCode
            ORDER BY [TeamNaam], [Prioriteit]
        ", conn);
        cmd.Parameters.AddWithValue("@dag", dagVanWeek);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var team = reader.GetString(0);
            var tijd = TimeOnly.FromTimeSpan(reader.GetTimeSpan(1));
            var prio = reader.GetInt32(2);
            if (!result.ContainsKey(team)) result[team] = new List<(TimeOnly, int)>();
            result[team].Add((tijd, prio));
        }
        return result;
    }
}
