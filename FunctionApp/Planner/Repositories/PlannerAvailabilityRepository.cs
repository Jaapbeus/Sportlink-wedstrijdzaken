using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Planner;

/// <summary>
/// Repository voor veldbeschikbaarheid en bezettingsqueries.
/// Extracted uit PlannerDataAccess (#474).
/// </summary>
internal static class PlannerAvailabilityRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    internal static async Task<List<VeldBeschikbaarheidInfo>> GetAvailableFieldsAsync(DateOnly date, string? clubCode = null)
    {
        var results = new List<VeldBeschikbaarheidInfo>();
        int dagVanWeek = ((int)date.DayOfWeek == 0) ? 7 : (int)date.DayOfWeek;
        clubCode ??= SystemUtilities.AppSettings.GetSetting("clubCode")
            ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT vb.[VeldNummer], vb.[BeschikbaarVanaf], vb.[BeschikbaarTot], vb.[GebruikZonsondergang]
            FROM [dbo].[VeldBeschikbaarheid] vb
            INNER JOIN [dbo].[Velden] v ON v.[VeldNummer] = vb.[VeldNummer]
            WHERE v.[Actief] = 1 AND vb.[DagVanWeek] = @dag AND vb.[ClubCode] = @clubCode
            ORDER BY vb.[VeldNummer]
        ", conn);
        cmd.Parameters.AddWithValue("@dag", dagVanWeek);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new VeldBeschikbaarheidInfo
            {
                VeldNummer = reader.GetInt32(0),
                BeschikbaarVanaf = TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)),
                BeschikbaarTot = TimeOnly.FromTimeSpan(reader.GetTimeSpan(2)),
                GebruikZonsondergang = reader.GetBoolean(3)
            });
        return results;
    }

    internal static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsAsync(DateOnly date)
    {
        var results = new List<BestaandeWedstrijd>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [Datum], [AanvangsTijd], [EindTijd], [VeldNummer], [VeldDeelGebruik],
                   [LeeftijdsCategorie], [TeamNaam], [Wedstrijd], [VeldSubpositie], [Bron]
            FROM (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY [VeldNummer], [AanvangsTijd], [Wedstrijd]
                    ORDER BY [Bron]
                ) AS rn
                FROM [planner].[AlleWedstrijdenOpVeld]
                WHERE [Datum] = @date
            ) sub WHERE rn = 1
        ", conn);
        cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var aanvangsTijd = reader.GetTimeSpan(1);
            var eindTijdDt   = reader.GetDateTime(2);
            results.Add(new BestaandeWedstrijd
            {
                Datum              = DateOnly.FromDateTime(reader.GetDateTime(0)),
                AanvangsTijd       = TimeOnly.FromTimeSpan(aanvangsTijd),
                EindTijd           = TimeOnly.FromDateTime(eindTijdDt),
                VeldNummer         = reader.GetInt32(3),
                VeldDeelGebruik    = reader.GetDecimal(4),
                LeeftijdsCategorie = reader.IsDBNull(5) ? null : reader.GetString(5),
                TeamNaam           = reader.IsDBNull(6) ? null : reader.GetString(6),
                Wedstrijd          = reader.IsDBNull(7) ? null : reader.GetString(7),
                VeldSubpositie     = reader.IsDBNull(8) ? null : reader.GetString(8)?.Trim(),
                Bron               = reader.GetString(9)
            });
        }
        return results;
    }

    internal static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingAsync(
        DateOnly date, long excludeWedstrijdcode)
    {
        var all = await GetFieldOccupationsAsync(date);
        return all.Where(o => o.Wedstrijd == null ||
            !o.Wedstrijd.Contains(excludeWedstrijdcode.ToString())).ToList();
    }

    internal static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingMatchAsync(
        DateOnly date, string wedstrijdNaam, TimeOnly aanvangsTijd, int veldNummer)
    {
        var all = await GetFieldOccupationsAsync(date);
        return all.Where(o =>
            !(o.VeldNummer == veldNummer &&
              o.AanvangsTijd == aanvangsTijd &&
              o.Wedstrijd != null && o.Wedstrijd.Trim() == wedstrijdNaam.Trim())
        ).ToList();
    }
}
