using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Planner;

/// <summary>
/// Repository voor ALLSTARS testdata en AVG-contactgegevens.
/// Extracted uit PlannerDataAccess (#474).
/// </summary>
internal static class AllstarsTestDataRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    // ALLSTARS testmodus: velden met VeldNummer >= 100
    internal static async Task<List<VeldInfo>> GetAllstarsVeldenAsync()
    {
        var results = new List<VeldInfo>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT [VeldNummer], [VeldNaam], ISNULL([VeldType], 'kunstgras') AS [VeldType], [HeeftKunstlicht] FROM [dbo].[Velden] WHERE [Actief] = 1 AND [VeldNummer] >= 100 ORDER BY [VeldNummer]", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new VeldInfo
            {
                VeldNummer = reader.GetInt32(0),
                VeldNaam = reader.GetString(1),
                VeldType = reader.GetString(2),
                HeeftKunstlicht = reader.GetBoolean(3)
            });
        return results;
    }

    internal static async Task<List<WedstrijdRaw>> GetAllMatchesForDatumAsync(DateOnly datum, string clubCode)
    {
        bool isAllstars = clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase);
        var results = new List<WedstrijdRaw>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();

        string sql = isAllstars
            ? @"SELECT m.[wedstrijdcode],
                       COALESCE(NULLIF(m.[wedstrijd], ''),
                                COALESCE(m.[teamnaam], '') + ' - ' + COALESCE(m.[uitteam], '')) AS wedstrijd,
                       m.[teamnaam], m.[uitteam],
                       m.[aanvangstijd], m.[veld], m.[competitiesoort],
                       NULL AS leeftijdscategorie
                FROM [his].[matches] m
                WHERE CAST(m.[kaledatum] AS DATE) = @date
                  AND m.[ClubCode] = 'ALLSTARS'
                  AND (m.[status] IS NULL OR m.[status] <> 'Afgelast')
                ORDER BY m.[teamnaam]"
            : @"SELECT m.[wedstrijdcode], m.[wedstrijd], m.[teamnaam], m.[uitteam],
                       m.[aanvangstijd], m.[veld], m.[competitiesoort],
                       REPLACE(REPLACE(REPLACE(ISNULL(t.[leeftijdscategorie], ''), 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR') AS leeftijdscategorie
                FROM [his].[matches] m
                LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[ClubCode] = m.[ClubCode]
                WHERE CAST(m.[kaledatum] AS DATE) = @date
                  AND m.[ClubCode] = @clubCode
                  AND m.[status] <> 'Afgelast'
                  AND m.[accommodatie] LIKE '%' + (SELECT TOP 1 [Accommodatie] FROM [dbo].[AppSettings] WHERE [ClubCode] = @clubCode) + '%'
                ORDER BY m.[teamvolgorde], m.[teamnaam]";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@date", datum.ToDateTime(TimeOnly.MinValue));
        if (!isAllstars) cmd.Parameters.AddWithValue("@clubCode", clubCode);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new WedstrijdRaw
            {
                WedstrijdCode = reader.IsDBNull(0) ? null : reader.GetInt64(0),
                Wedstrijd = reader.IsDBNull(1) ? "" : reader.GetString(1),
                TeamNaam = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Uitteam = reader.IsDBNull(3) ? null : reader.GetString(3),
                AanvangsTijd = reader.IsDBNull(4) ? null : reader.GetString(4)?.Trim(),
                Veld = reader.IsDBNull(5) ? null : reader.GetString(5)?.Trim(),
                Competitiesoort = reader.IsDBNull(6) ? null : reader.GetString(6),
                LeeftijdsCategorie = reader.IsDBNull(7) ? null :
                    (string.IsNullOrWhiteSpace(reader.GetString(7)) ? null : reader.GetString(7))
            });
        return results;
    }

    internal static async Task<int> UpdateAllstarsMatchAsync(long wedstrijdCode, string nieuweVeld, string nieuweTijd)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [his].[matches]
            SET [aanvangstijd] = @tijd, [veld] = @veld, [mta_modified] = GETUTCDATE()
            WHERE [wedstrijdcode] = @code AND [ClubCode] = 'ALLSTARS'
        ", conn);
        cmd.Parameters.AddWithValue("@tijd", nieuweTijd);
        cmd.Parameters.AddWithValue("@veld", nieuweVeld);
        cmd.Parameters.AddWithValue("@code", wedstrijdCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    // AVG: bevat persoonsgegevens — gebruik alleen voor interne notificaties
    internal static async Task<TeamleiderContact?> GetTeamleiderContactAsync(string teamNaam)
    {
        var parts = teamNaam.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var teamZonderPrefix = parts.Length > 1 ? parts[1] : teamNaam;
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT TOP 1 Naam, Emailadres
            FROM [avg].[Teambegeleiding]
            WHERE (Team = @exactTeam OR Team LIKE @partialPattern)
              AND Emailadres IS NOT NULL AND Emailadres <> ''
              AND (Teamrol LIKE '%Trainer%' OR Teamrol LIKE '%Coach%'
                   OR Teamrol LIKE '%Teamleider%' OR Teamrol LIKE '%leider%')
            ORDER BY
                CASE WHEN Team = @exactTeam THEN 0 ELSE 1 END,
                CASE WHEN Teamrol LIKE '%Trainer%' THEN 1
                     WHEN Teamrol LIKE '%Coach%' THEN 2
                     ELSE 3 END
        ", conn);
        cmd.Parameters.AddWithValue("@exactTeam", teamNaam);
        cmd.Parameters.AddWithValue("@partialPattern", $"%{teamZonderPrefix}%");
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return new TeamleiderContact
            {
                Naam = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Emailadres = reader.IsDBNull(1) ? "" : reader.GetString(1)
            };
        return null;
    }
}
