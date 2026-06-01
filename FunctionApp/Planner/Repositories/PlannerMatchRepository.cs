using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner;

/// <summary>
/// Repository voor wedstrijd-opzoeken, plannen, herplannen en vervallen markeren.
/// Extracted uit PlannerDataAccess (#474).
/// </summary>
internal static class PlannerMatchRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    internal static async Task<List<BestaandeWedstrijd>> GetTeamMatchesOnDateAsync(string teamNaam, DateOnly date)
    {
        var results = new List<BestaandeWedstrijd>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT
                CAST(m.[kaledatum] AS DATE) AS Datum,
                CAST(m.[aanvangstijd] AS TIME) AS AanvangsTijd,
                ISNULL(s.[WedstrijdTotaal], 0) AS DuurMinuten,
                v.[VeldNummer], v.[VeldNaam], m.[wedstrijd], 'Competitie' AS Bron
            FROM [his].[matches] m
            LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam]
            LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
            LEFT JOIN [dbo].[Velden] v ON RTRIM(LEFT(m.[veld], 6)) = v.[VeldNaam]
            WHERE CAST(m.[kaledatum] AS DATE) = @date
              AND m.[status] <> 'Afgelast'
              AND m.[teamnaam] = @exactTeamNaam
            UNION ALL
            SELECT gw.[Datum], gw.[AanvangsTijd], gw.[WedstrijdDuurMinuten],
                   gw.[VeldNummer], v.[VeldNaam],
                   COALESCE(gw.[TeamNaam], '') + ' - ' + COALESCE(gw.[Tegenstander], ''), 'Planner'
            FROM [planner].[GeplandeWedstrijden] gw
            LEFT JOIN [dbo].[Velden] v ON v.[VeldNummer] = gw.[VeldNummer]
            WHERE gw.[Datum] = @date
              AND gw.[Status] <> 'Geannuleerd'
              AND gw.[TeamNaam] = @exactTeamNaam
        ", conn);
        cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@exactTeamNaam", teamNaam);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var aanvangsTijd = reader.GetTimeSpan(1);
            var duur = reader.GetInt32(2);
            if (duur <= 0)
            {
                var naam = reader.IsDBNull(5) ? "onbekend" : reader.GetString(5);
                throw new InvalidOperationException($"Speelduur niet geconfigureerd voor wedstrijd '{naam}'. Voeg de leeftijdscategorie toe aan dbo.Speeltijden via /instellingen/speeltijden.");
            }
            results.Add(new BestaandeWedstrijd
            {
                Datum        = DateOnly.FromDateTime(reader.GetDateTime(0)),
                AanvangsTijd = TimeOnly.FromTimeSpan(aanvangsTijd),
                EindTijd     = TimeOnly.FromTimeSpan(aanvangsTijd).AddMinutes(duur),
                VeldNummer   = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Wedstrijd    = reader.IsDBNull(5) ? null : reader.GetString(5),
                Bron         = reader.GetString(6)
            });
        }
        return results;
    }

    internal static async Task<List<BestaandeWedstrijd>> GetGeplandeWedstrijdenOnlyAsync(DateOnly date)
    {
        var results = new List<BestaandeWedstrijd>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT gw.[Datum], gw.[AanvangsTijd], gw.[EindTijd],
                   gw.[VeldNummer], gw.[VeldDeelGebruik], gw.[LeeftijdsCategorie],
                   gw.[TeamNaam],
                   COALESCE(gw.[TeamNaam], '') + ' - ' + COALESCE(gw.[Tegenstander], '') AS Wedstrijd,
                   v.[VeldNaam], 'Planner' AS Bron
            FROM [planner].[GeplandeWedstrijden] gw
            LEFT JOIN [dbo].[Velden] v ON v.[VeldNummer] = gw.[VeldNummer]
            WHERE gw.[Datum] = @date
              AND gw.[Status] <> 'Geannuleerd'
              AND gw.[IsVervallen] = 0", conn);
        cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new BestaandeWedstrijd
            {
                Datum              = DateOnly.FromDateTime(reader.GetDateTime(0)),
                AanvangsTijd       = TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)),
                EindTijd           = TimeOnly.FromDateTime(reader.GetDateTime(2)),
                VeldNummer         = reader.GetInt32(3),
                VeldDeelGebruik    = reader.GetDecimal(4),
                LeeftijdsCategorie = reader.IsDBNull(5) ? null : reader.GetString(5),
                TeamNaam           = reader.IsDBNull(6) ? null : reader.GetString(6),
                Wedstrijd          = reader.IsDBNull(7) ? null : reader.GetString(7),
                VeldSubpositie     = null,
                Bron               = "Planner"
            });
        return results;
    }

    internal static async Task<ZoekWedstrijdResponse?> FindMatchAsync(string teamNaam, DateOnly date)
    {
        var accommodatie = SystemUtilities.AppSettings.GetSetting("accommodatie")
            ?? throw new InvalidOperationException("Vereiste instelling 'accommodatie' ontbreekt in dbo.AppSettings");
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT TOP 1
                CAST(m.[wedstrijdcode] AS BIGINT), m.[wedstrijd],
                CAST(m.[kaledatum] AS DATE), m.[aanvangstijd],
                ISNULL(s.[WedstrijdTotaal], 0), m.[veld],
                t.[leeftijdscategorie], COALESCE(s.[Veldafmeting], 1.00)
            FROM [his].[matches] m
            LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
            LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
            WHERE CAST(m.[kaledatum] AS DATE) = @date
              AND m.[accommodatie] LIKE @accommodatiePattern
              AND m.[status] <> 'Afgelast'
              AND (m.[teamnaam] LIKE @teamPattern OR m.[wedstrijd] LIKE @teamPattern)
            ORDER BY m.[aanvangstijd]
        ", conn);
        cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@teamPattern", $"%{teamNaam}%");
        cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{accommodatie}%");
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var aanvangstijd = reader.GetString(3).Trim();
            var duur = reader.GetInt32(4);
            var naam = reader.GetString(1).Trim();
            if (duur <= 0) throw new InvalidOperationException($"Speelduur niet geconfigureerd voor wedstrijd '{naam}'. Voeg de leeftijdscategorie toe aan dbo.Speeltijden via /instellingen/speeltijden.");
            TimeOnly.TryParse(aanvangstijd, out var start);
            return new ZoekWedstrijdResponse
            {
                Wedstrijdcode      = reader.GetInt64(0),
                Wedstrijd          = naam,
                Datum              = date.ToString("yyyy-MM-dd"),
                AanvangsTijd       = aanvangstijd,
                EindTijd           = start.AddMinutes(duur).ToString("HH:mm"),
                DuurMinuten        = duur,
                VeldNaam           = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                LeeftijdsCategorie = reader.IsDBNull(6) ? null : reader.GetString(6).Trim(),
                VeldDeelGebruik    = reader.GetDecimal(7)
            };
        }
        return null;
    }

    internal static async Task<ZoekWedstrijdResponse?> FindMatchByOpponentAsync(string tegenstander, DateOnly? datum)
    {
        var accommodatie = SystemUtilities.AppSettings.GetSetting("accommodatie")
            ?? throw new InvalidOperationException("Vereiste instelling 'accommodatie' ontbreekt in dbo.AppSettings");
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();

        // Zoek in his.matches
        using (var cmd = new SqlCommand(@"
            SELECT TOP 1
                CAST(m.[wedstrijdcode] AS BIGINT), m.[wedstrijd],
                CAST(m.[kaledatum] AS DATE), m.[aanvangstijd],
                ISNULL(s.[WedstrijdTotaal], 0), m.[veld],
                t.[leeftijdscategorie], COALESCE(s.[Veldafmeting], 1.00)
            FROM [his].[matches] m
            LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
            LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
            WHERE m.[accommodatie] LIKE @accommodatiePattern
              AND m.[status] <> 'Afgelast'
              AND m.[wedstrijd] LIKE @tegPattern
              AND (@datum IS NULL OR CAST(m.[kaledatum] AS DATE) = @datum)
            ORDER BY m.[kaledatum]
        ", conn))
        {
            cmd.Parameters.AddWithValue("@tegPattern", $"%{tegenstander}%");
            cmd.Parameters.Add("@datum", System.Data.SqlDbType.Date).Value =
                datum.HasValue ? datum.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
            cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{accommodatie}%");
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var aanvangstijd = reader.GetString(3).Trim();
                var duur = reader.GetInt32(4);
                var naam = reader.GetString(1).Trim();
                if (duur <= 0) throw new InvalidOperationException($"Speelduur niet geconfigureerd voor wedstrijd '{naam}'. Voeg de leeftijdscategorie toe aan dbo.Speeltijden via /instellingen/speeltijden.");
                var datumResult = DateOnly.FromDateTime(reader.GetDateTime(2));
                TimeOnly.TryParse(aanvangstijd, out var start);
                return new ZoekWedstrijdResponse
                {
                    Wedstrijdcode = reader.GetInt64(0), Wedstrijd = naam,
                    Datum = datumResult.ToString("yyyy-MM-dd"), AanvangsTijd = aanvangstijd,
                    EindTijd = start.AddMinutes(duur).ToString("HH:mm"), DuurMinuten = duur,
                    VeldNaam = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                    LeeftijdsCategorie = reader.IsDBNull(6) ? null : reader.GetString(6).Trim(),
                    VeldDeelGebruik = reader.GetDecimal(7)
                };
            }
        }

        // Zoek in planner.GeplandeWedstrijden
        using (var cmd2 = new SqlCommand(@"
            SELECT TOP 1
                CAST(0 AS BIGINT),
                COALESCE(gw.[TeamNaam], '') + ' - ' + COALESCE(gw.[Tegenstander], ''),
                CAST(gw.[Datum] AS DATE),
                CONVERT(VARCHAR(8), gw.[AanvangsTijd], 108),
                gw.[WedstrijdDuurMinuten],
                COALESCE(v.[VeldNaam], ''),
                gw.[LeeftijdsCategorie],
                CAST(1.00 AS DECIMAL(18,2))
            FROM [planner].[GeplandeWedstrijden] gw
            LEFT JOIN [dbo].[Velden] v ON v.[VeldNummer] = gw.[VeldNummer]
            WHERE gw.[Status] <> 'Geannuleerd'
              AND gw.[Tegenstander] LIKE @tegPattern
              AND (@datum IS NULL OR gw.[Datum] = @datum)
            ORDER BY gw.[Datum]
        ", conn))
        {
            cmd2.Parameters.AddWithValue("@tegPattern", $"%{tegenstander}%");
            cmd2.Parameters.Add("@datum", System.Data.SqlDbType.Date).Value =
                datum.HasValue ? datum.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
            using var reader2 = await cmd2.ExecuteReaderAsync();
            if (await reader2.ReadAsync())
            {
                var aanvangstijd = reader2.GetString(3).Trim();
                var duur = reader2.GetInt32(4);
                var datumResult = DateOnly.FromDateTime(reader2.GetDateTime(2));
                TimeOnly.TryParse(aanvangstijd, out var start);
                return new ZoekWedstrijdResponse
                {
                    Wedstrijdcode = reader2.GetInt64(0), Wedstrijd = reader2.GetString(1).Trim(),
                    Datum = datumResult.ToString("yyyy-MM-dd"), AanvangsTijd = aanvangstijd,
                    EindTijd = start.AddMinutes(duur).ToString("HH:mm"), DuurMinuten = duur,
                    VeldNaam = reader2.IsDBNull(5) ? null : reader2.GetString(5).Trim(),
                    LeeftijdsCategorie = reader2.IsDBNull(6) ? null : reader2.GetString(6).Trim(),
                    VeldDeelGebruik = reader2.GetDecimal(7)
                };
            }
        }
        return null;
    }

    internal static async Task<ZoekWedstrijdResponse?> FindMatchByCodeAsync(long wedstrijdcode)
    {
        var accommodatie = SystemUtilities.AppSettings.GetSetting("accommodatie")
            ?? throw new InvalidOperationException("Vereiste instelling 'accommodatie' ontbreekt in dbo.AppSettings");
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT TOP 1
                CAST(m.[wedstrijdcode] AS BIGINT), m.[wedstrijd],
                CAST(m.[kaledatum] AS DATE), m.[aanvangstijd],
                ISNULL(s.[WedstrijdTotaal], 0), m.[veld],
                t.[leeftijdscategorie], COALESCE(s.[Veldafmeting], 1.00)
            FROM [his].[matches] m
            LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
            LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
            WHERE CAST(m.[wedstrijdcode] AS BIGINT) = @code
              AND m.[accommodatie] LIKE @accommodatiePattern
        ", conn);
        cmd.Parameters.AddWithValue("@code", wedstrijdcode);
        cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{accommodatie}%");
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var aanvangstijd = reader.GetString(3).Trim();
            var duur = reader.GetInt32(4);
            var naam = reader.GetString(1).Trim();
            if (duur <= 0) throw new InvalidOperationException($"Speelduur niet geconfigureerd voor wedstrijd '{naam}'. Voeg de leeftijdscategorie toe aan dbo.Speeltijden via /instellingen/speeltijden.");
            var datum = DateOnly.FromDateTime(reader.GetDateTime(2));
            TimeOnly.TryParse(aanvangstijd, out var start);
            return new ZoekWedstrijdResponse
            {
                Wedstrijdcode = reader.GetInt64(0), Wedstrijd = naam,
                Datum = datum.ToString("yyyy-MM-dd"), AanvangsTijd = aanvangstijd,
                EindTijd = start.AddMinutes(duur).ToString("HH:mm"), DuurMinuten = duur,
                VeldNaam = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                LeeftijdsCategorie = reader.IsDBNull(6) ? null : reader.GetString(6).Trim(),
                VeldDeelGebruik = reader.GetDecimal(7)
            };
        }
        return null;
    }

    internal static async Task<int> SavePlannedMatchAsync(
        DateOnly datum, TimeOnly aanvangsTijd, TimeOnly eindTijd, int veldNummer,
        decimal veldDeelGebruik, string? leeftijdsCategorie, string? teamNaam,
        string? tegenstander, int wedstrijdDuurMinuten, string? aangevraagdDoor,
        string? clubCode = null)
    {
        var cc = clubCode ?? SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [planner].[GeplandeWedstrijden]
                ([Datum], [AanvangsTijd], [EindTijd], [VeldNummer], [VeldDeelGebruik],
                 [LeeftijdsCategorie], [TeamNaam], [Tegenstander], [WedstrijdDuurMinuten],
                 [Status], [AangevraagdDoor], [ClubCode])
            OUTPUT INSERTED.[Id]
            VALUES (@datum, @aanvang, @eind, @veld, @deel, @cat, @team, @tegen, @duur, 'Te bevestigen', @door, @cc)
        ", conn);
        cmd.Parameters.AddWithValue("@datum", datum.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@aanvang", aanvangsTijd.ToTimeSpan());
        cmd.Parameters.AddWithValue("@eind", eindTijd.ToTimeSpan());
        cmd.Parameters.AddWithValue("@veld", veldNummer);
        cmd.Parameters.AddWithValue("@deel", veldDeelGebruik);
        cmd.Parameters.AddWithValue("@cat", (object?)leeftijdsCategorie ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@team", (object?)teamNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tegen", (object?)tegenstander ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@duur", wedstrijdDuurMinuten);
        cmd.Parameters.AddWithValue("@door", (object?)aangevraagdDoor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cc", cc);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> SaveHerplanVerzoekAsync(
        long wedstrijdcode, string huidigeWedstrijd, DateOnly huidigeDatum,
        TimeOnly huidigeAanvangsTijd, string? huidigeVeldNaam,
        TimeOnly gewensteAanvangsTijd, int? gewenstVeldNummer,
        string? aangevraagdDoor, string? opmerking)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [planner].[HerplanVerzoeken]
                ([Wedstrijdcode], [HuidigeWedstrijd], [HuidigeDatum], [HuidigeAanvangsTijd],
                 [HuidigeVeldNaam], [GewensteAanvangsTijd], [GewenstVeldNummer],
                 [Status], [AangevraagdDoor], [Opmerking])
            OUTPUT INSERTED.[Id]
            VALUES (@code, @wedstrijd, @datum, @aanvang, @veld, @gewensteTijd, @gewenstVeld, 'Aangevraagd', @door, @opmerking)
        ", conn);
        cmd.Parameters.AddWithValue("@code", wedstrijdcode);
        cmd.Parameters.AddWithValue("@wedstrijd", huidigeWedstrijd);
        cmd.Parameters.AddWithValue("@datum", huidigeDatum.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@aanvang", huidigeAanvangsTijd.ToTimeSpan());
        cmd.Parameters.AddWithValue("@veld", (object?)huidigeVeldNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@gewensteTijd", gewensteAanvangsTijd.ToTimeSpan());
        cmd.Parameters.AddWithValue("@gewenstVeld", (object?)gewenstVeldNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@door", (object?)aangevraagdDoor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@opmerking", (object?)opmerking ?? DBNull.Value);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task MarkeerVervallenGeplandeWedstrijdenAsync(ILogger log)
    {
        var accommodatie = SystemUtilities.AppSettings.GetSetting("accommodatie");
        if (string.IsNullOrWhiteSpace(accommodatie))
        {
            log.LogWarning("Instelling 'accommodatie' niet geconfigureerd — MarkeerVervallenGeplandeWedstrijden overgeslagen. Stel de accommodatienaam in via Admin GUI → Instellingen.");
            return;
        }
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE gw
            SET gw.[IsVervallen] = 1,
                gw.[SportlinkWedstrijdCode] = CAST(m.[wedstrijdcode] AS BIGINT),
                gw.[mta_modified] = GETUTCDATE()
            FROM [planner].[GeplandeWedstrijden] gw
            INNER JOIN [his].[matches] m
                ON CAST(m.[kaledatum] AS DATE) = gw.[Datum]
                AND m.[teamnaam] = gw.[TeamNaam]
            WHERE gw.[IsVervallen] = 0
              AND gw.[Status] <> 'Geannuleerd'
              AND m.[accommodatie] LIKE @accommodatiePattern
        ", conn);
        cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{accommodatie}%");
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows > 0)
            log.LogInformation("Post-sync: {Count} geplande wedstrijd(en) als vervallen gemarkeerd", rows);
    }

    internal static async Task<bool> TeamExistsAsync(string team)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM [his].[teams] WHERE UPPER([teamnaam]) = UPPER(@team)", conn);
        cmd.Parameters.AddWithValue("@team", team);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    internal static async Task<List<TeamScheduleWedstrijd>> GetFutureMatchesForTeamAsync(
        string team, DateOnly van, DateOnly tot)
    {
        var results = new List<TeamScheduleWedstrijd>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();

        using (var cmd = new SqlCommand(@"
            SELECT CAST(m.[kaledatum] AS DATE), m.[aanvangstijd],
                   m.[thuisteam], m.[uitteam], m.[competitiesoort], m.[veld],
                   CAST(m.[wedstrijdcode] AS BIGINT)
            FROM [his].[matches] m
            WHERE CAST(m.[kaledatum] AS DATE) BETWEEN @van AND @tot
              AND m.[status] <> 'Afgelast'
              AND (UPPER(m.[teamnaam]) = UPPER(@team))
            ORDER BY m.[kaledatum], m.[aanvangstijd]
        ", conn))
        {
            cmd.Parameters.AddWithValue("@van", van.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("@tot", tot.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("@team", team);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var datum = DateOnly.FromDateTime(reader.GetDateTime(0));
                var aanvang = reader.GetString(1).Trim();
                var thuisTeam = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
                var uitTeam = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim();
                bool isThuis = thuisTeam.Equals(team, StringComparison.OrdinalIgnoreCase);
                results.Add(new TeamScheduleWedstrijd
                {
                    Datum = datum.ToString("yyyy-MM-dd"), AanvangsTijd = aanvang,
                    ThuisUit = isThuis ? "thuis" : "uit",
                    Tegenstander = isThuis ? uitTeam : thuisTeam,
                    Type = DetermineMatchType(reader.IsDBNull(4) ? "" : reader.GetString(4)),
                    Veld = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                    Wedstrijdcode = reader.GetInt64(6)
                });
            }
        }

        using (var cmd2 = new SqlCommand(@"
            SELECT gw.[Datum], CONVERT(VARCHAR(8), gw.[AanvangsTijd], 108),
                   gw.[Tegenstander], v.[VeldNaam]
            FROM [planner].[GeplandeWedstrijden] gw
            LEFT JOIN [dbo].[Velden] v ON v.[VeldNummer] = gw.[VeldNummer]
            WHERE gw.[Datum] BETWEEN @van AND @tot
              AND gw.[Status] <> 'Geannuleerd'
              AND UPPER(gw.[TeamNaam]) = UPPER(@team)
            ORDER BY gw.[Datum], gw.[AanvangsTijd]
        ", conn))
        {
            cmd2.Parameters.AddWithValue("@van", van.ToDateTime(TimeOnly.MinValue));
            cmd2.Parameters.AddWithValue("@tot", tot.ToDateTime(TimeOnly.MinValue));
            cmd2.Parameters.AddWithValue("@team", team);
            using var reader2 = await cmd2.ExecuteReaderAsync();
            while (await reader2.ReadAsync())
                results.Add(new TeamScheduleWedstrijd
                {
                    Datum = DateOnly.FromDateTime(reader2.GetDateTime(0)).ToString("yyyy-MM-dd"),
                    AanvangsTijd = reader2.GetString(1), ThuisUit = "thuis",
                    Tegenstander = reader2.IsDBNull(2) ? "" : reader2.GetString(2),
                    Type = "oefenwedstrijd",
                    Veld = reader2.IsDBNull(3) ? null : reader2.GetString(3),
                    Wedstrijdcode = null
                });
        }

        results.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Datum, b.Datum, StringComparison.Ordinal);
            return cmp != 0 ? cmp : string.Compare(a.AanvangsTijd, b.AanvangsTijd, StringComparison.Ordinal);
        });
        return results;
    }

    private static string DetermineMatchType(string competitiesoort)
    {
        if (string.IsNullOrWhiteSpace(competitiesoort)) return "competitie";
        var lower = competitiesoort.ToLowerInvariant();
        if (lower.Contains("oefen")) return "oefenwedstrijd";
        if (lower.Contains("beker")) return "beker";
        return "competitie";
    }
}
