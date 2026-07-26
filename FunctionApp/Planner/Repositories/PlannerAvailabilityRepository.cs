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

    /// <summary>
    /// Veldbezetting op één datum, hard gescoped op ClubCode (#580).
    /// De view <c>planner.AlleWedstrijdenOpVeld</c> levert ClubCode per rij; zonder dit
    /// filter mengt demodata (ALLSTARS) met productiedata en worden bezettingsslots
    /// onjuist berekend. Trainingsblokken (<see cref="GetTrainingOccupationsAsync"/>) zijn een
    /// derde, club-vrij-instelbare bezettingsbron naast wedstrijden (#679).
    /// </summary>
    internal static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsAsync(
        DateOnly date, string? clubCode = null)
    {
        var results = new List<BestaandeWedstrijd>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT [Datum], [AanvangsTijd], [EindTijd], [VeldNummer], [VeldDeelGebruik],
                   [LeeftijdsCategorie], [TeamNaam], [Wedstrijd], [VeldSubpositie], [Bron],
                   [Wedstrijdcode]
            FROM (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY [VeldNummer], [AanvangsTijd], [Wedstrijd]
                    ORDER BY [Bron]
                ) AS rn
                FROM [planner].[AlleWedstrijdenOpVeld]
                WHERE [Datum] = @date
                  AND [ClubCode] = {ClubScope.ClubCodeParam}
            ) sub WHERE rn = 1
        ", conn);
        cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        ClubScope.AddClubParam(cmd, clubCode);
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
                Bron               = reader.GetString(9),
                Wedstrijdcode      = reader.IsDBNull(10) ? null : reader.GetInt64(10)
            });
        }
        results.AddRange(await GetTrainingOccupationsAsync(date, clubCode));
        return results;
    }

    /// <summary>
    /// Terugkerende trainingsbezetting uit dbo.VeldTraining voor de weekdag van <paramref name="date"/>.
    /// Vrij per club instelbaar (#679, spoor B): een club zonder rijen in deze tabel houdt
    /// exact het gedrag van vóór deze feature — geen impliciete bezetting.
    /// </summary>
    internal static async Task<List<BestaandeWedstrijd>> GetTrainingOccupationsAsync(
        DateOnly date, string? clubCode = null)
    {
        var results = new List<BestaandeWedstrijd>();
        int dagVanWeek = ((int)date.DayOfWeek == 0) ? 7 : (int)date.DayOfWeek;
        var resolvedClubCode = ClubScope.Resolve(clubCode);
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT t.[VeldNummer], t.[VanTijd], t.[TotTijd], t.[Omschrijving]
            FROM [dbo].[VeldTraining] t
            INNER JOIN [dbo].[Velden] v ON v.[VeldNummer] = t.[VeldNummer]
            WHERE v.[Actief] = 1 AND t.[Actief] = 1 AND t.[DagVanWeek] = @dag AND t.[ClubCode] = @clubCode
            ORDER BY t.[VeldNummer], t.[VanTijd]
        ", conn);
        cmd.Parameters.AddWithValue("@dag", dagVanWeek);
        cmd.Parameters.AddWithValue("@clubCode", resolvedClubCode);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new BestaandeWedstrijd
            {
                Datum           = date,
                AanvangsTijd    = TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)),
                EindTijd        = TimeOnly.FromTimeSpan(reader.GetTimeSpan(2)),
                VeldNummer      = reader.GetInt32(0),
                VeldDeelGebruik = 1.00m,
                Wedstrijd       = reader.IsDBNull(3) ? "Training" : reader.GetString(3),
                Bron            = "Training"
            });
        return results;
    }

    internal static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingAsync(
        DateOnly date, long excludeWedstrijdcode, string? clubCode = null)
    {
        var all = await GetFieldOccupationsAsync(date, clubCode);
        return FilterExcludingWedstrijdcode(all, excludeWedstrijdcode);
    }

    /// <summary>
    /// Sluit exact één wedstrijd uit op wedstrijdcode (#574).
    /// Nooit op tekst-contains in de wedstrijdnaam: code 123 matcht dan ook 3123, en de
    /// filtering valt stilzwijgend om als de opmaak van de wedstrijdnaam verandert.
    /// Rijen zonder wedstrijdcode (planner-slots zonder Sportlink-tegenhanger) blijven staan.
    /// </summary>
    internal static List<BestaandeWedstrijd> FilterExcludingWedstrijdcode(
        List<BestaandeWedstrijd> occupations, long excludeWedstrijdcode)
        => occupations.Where(o => o.Wedstrijdcode != excludeWedstrijdcode).ToList();

    internal static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingMatchAsync(
        DateOnly date, string wedstrijdNaam, TimeOnly aanvangsTijd, int veldNummer,
        string? clubCode = null)
    {
        var all = await GetFieldOccupationsAsync(date, clubCode);
        return all.Where(o =>
            !(o.VeldNummer == veldNummer &&
              o.AanvangsTijd == aanvangsTijd &&
              o.Wedstrijd != null && o.Wedstrijd.Trim() == wedstrijdNaam.Trim())
        ).ToList();
    }
}
