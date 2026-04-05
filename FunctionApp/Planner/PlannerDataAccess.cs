using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Planner
{
    public static class PlannerDataAccess
    {
        private static string ConnectionString => SystemUtilities.DatabaseConfig.ConnectionString;

        public static async Task<Speeltijd?> GetSpeeltijdAsync(string leeftijdsCategorie)
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [Leeftijd], [Veldafmeting], [WedstrijdTotaal] FROM [dbo].[Speeltijden] WHERE [Leeftijd] = @cat", conn);
            cmd.Parameters.AddWithValue("@cat", leeftijdsCategorie);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new Speeltijd
                {
                    Leeftijd = reader.GetString(0),
                    Veldafmeting = reader.GetDecimal(1),
                    WedstrijdTotaal = reader.GetInt32(2)
                };
            }
            return null;
        }

        public static async Task<List<BestaandeWedstrijd>> GetTeamMatchesOnDateAsync(string teamNaam, DateOnly date)
        {
            var results = new List<BestaandeWedstrijd>();
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();

            // Zoeken in his.matches voor dit team op deze datum (thuis of uit, op teamnaam of wedstrijdomschrijving)
            using var cmd = new SqlCommand(@"
                SELECT
                    CAST(m.[kaledatum] AS DATE) AS Datum,
                    CAST(m.[aanvangstijd] AS TIME) AS AanvangsTijd,
                    COALESCE(CAST(md.[Duration] AS INT), s.[WedstrijdTotaal], 105) AS DuurMinuten,
                    v.[VeldNummer],
                    v.[VeldNaam],
                    m.[wedstrijd],
                    'Competitie' AS Bron
                FROM [his].[matches] m
                LEFT JOIN [his].[matchdetails] md ON CAST(md.[InternCode] AS BIGINT) = CAST(m.[wedstrijdcode] AS BIGINT)
                LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam]
                LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = t.[leeftijdscategorie]
                LEFT JOIN [dbo].[Velden] v ON RTRIM(LEFT(m.[veld], 6)) = v.[VeldNaam]
                WHERE CAST(m.[kaledatum] AS DATE) = @date
                  AND m.[status] <> 'Afgelast'
                  AND (m.[teamnaam] LIKE @teamPattern OR m.[wedstrijd] LIKE @teamPattern)

                UNION ALL

                SELECT
                    gw.[Datum],
                    gw.[AanvangsTijd],
                    gw.[WedstrijdDuurMinuten] AS DuurMinuten,
                    gw.[VeldNummer],
                    v.[VeldNaam],
                    COALESCE(gw.[TeamNaam], '') + ' - ' + COALESCE(gw.[Tegenstander], '') AS wedstrijd,
                    'Planner' AS Bron
                FROM [planner].[GeplandeWedstrijden] gw
                LEFT JOIN [dbo].[Velden] v ON v.[VeldNummer] = gw.[VeldNummer]
                WHERE gw.[Datum] = @date
                  AND gw.[Status] <> 'Geannuleerd'
                  AND (gw.[TeamNaam] LIKE @teamPattern OR gw.[Tegenstander] LIKE @teamPattern)
            ", conn);
            cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("@teamPattern", $"%{teamNaam}%");

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var aanvangsTijd = reader.GetTimeSpan(1);
                var duur = reader.GetInt32(2);
                results.Add(new BestaandeWedstrijd
                {
                    Datum = DateOnly.FromDateTime(reader.GetDateTime(0)),
                    AanvangsTijd = TimeOnly.FromTimeSpan(aanvangsTijd),
                    EindTijd = TimeOnly.FromTimeSpan(aanvangsTijd).AddMinutes(duur),
                    VeldNummer = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    Wedstrijd = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Bron = reader.GetString(6)
                });
            }
            return results;
        }

        public static async Task<List<VeldBeschikbaarheidInfo>> GetAvailableFieldsAsync(DateOnly date)
        {
            var results = new List<VeldBeschikbaarheidInfo>();
            // DayOfWeek: .NET Maandag=1 komt overeen met onze DB-conventie (1=Maandag...7=Zondag)
            int dagVanWeek = ((int)date.DayOfWeek == 0) ? 7 : (int)date.DayOfWeek;

            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT vb.[VeldNummer], vb.[BeschikbaarVanaf], vb.[BeschikbaarTot], vb.[GebruikZonsondergang]
                FROM [dbo].[VeldBeschikbaarheid] vb
                INNER JOIN [dbo].[Velden] v ON v.[VeldNummer] = vb.[VeldNummer]
                WHERE v.[Actief] = 1 AND vb.[DagVanWeek] = @dag
                ORDER BY vb.[VeldNummer]
            ", conn);
            cmd.Parameters.AddWithValue("@dag", dagVanWeek);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new VeldBeschikbaarheidInfo
                {
                    VeldNummer = reader.GetInt32(0),
                    BeschikbaarVanaf = TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)),
                    BeschikbaarTot = TimeOnly.FromTimeSpan(reader.GetTimeSpan(2)),
                    GebruikZonsondergang = reader.GetBoolean(3)
                });
            }
            return results;
        }

        public static async Task<List<VeldInfo>> GetVeldenAsync()
        {
            var results = new List<VeldInfo>();
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [VeldNummer], [VeldNaam], ISNULL([VeldType], 'kunstgras') AS [VeldType], [HeeftKunstlicht] FROM [dbo].[Velden] WHERE [Actief] = 1 ORDER BY [VeldNummer]", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new VeldInfo
                {
                    VeldNummer = reader.GetInt32(0),
                    VeldNaam = reader.GetString(1),
                    VeldType = reader.GetString(2),
                    HeeftKunstlicht = reader.GetBoolean(3)
                });
            }
            return results;
        }

        public static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsAsync(DateOnly date)
        {
            var results = new List<BestaandeWedstrijd>();
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT [Datum], [AanvangsTijd], [EindTijd], [VeldNummer], [VeldDeelGebruik],
                       [LeeftijdsCategorie], [TeamNaam], [Wedstrijd], [Bron]
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
                var eindTijdDt = reader.GetDateTime(2);
                results.Add(new BestaandeWedstrijd
                {
                    Datum = DateOnly.FromDateTime(reader.GetDateTime(0)),
                    AanvangsTijd = TimeOnly.FromTimeSpan(aanvangsTijd),
                    EindTijd = TimeOnly.FromDateTime(eindTijdDt),
                    VeldNummer = reader.GetInt32(3),
                    VeldDeelGebruik = reader.GetDecimal(4),
                    LeeftijdsCategorie = reader.IsDBNull(5) ? null : reader.GetString(5),
                    TeamNaam = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Wedstrijd = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Bron = reader.GetString(8)
                });
            }
            return results;
        }

        public static async Task<List<TeamRegel>> GetTeamRulesAsync(string teamNaam)
        {
            var results = new List<TeamRegel>();
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT [TeamNaam], [RegelType], [WaardeMinuten], [WaardeVeldNummer], [WaardeTijd], [Prioriteit]
                FROM [dbo].[TeamRegels]
                WHERE [TeamNaam] = @team AND [Actief] = 1
                ORDER BY [Prioriteit] DESC
            ", conn);
            cmd.Parameters.AddWithValue("@team", teamNaam);

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

        public static async Task<TimeOnly?> GetSunsetAsync(DateOnly date)
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [Zonsondergang] FROM [dbo].[Zonsondergang] WHERE [Datum] = @date", conn);
            cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
            var result = await cmd.ExecuteScalarAsync();
            if (result is TimeSpan ts)
                return TimeOnly.FromTimeSpan(ts);
            return null;
        }

        public static async Task<int> SavePlannedMatchAsync(
            DateOnly datum, TimeOnly aanvangsTijd, TimeOnly eindTijd, int veldNummer,
            decimal veldDeelGebruik, string? leeftijdsCategorie, string? teamNaam,
            string? tegenstander, int wedstrijdDuurMinuten, string? aangevraagdDoor)
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                INSERT INTO [planner].[GeplandeWedstrijden]
                    ([Datum], [AanvangsTijd], [EindTijd], [VeldNummer], [VeldDeelGebruik],
                     [LeeftijdsCategorie], [TeamNaam], [Tegenstander], [WedstrijdDuurMinuten],
                     [Status], [AangevraagdDoor])
                OUTPUT INSERTED.[Id]
                VALUES
                    (@datum, @aanvang, @eind, @veld, @deel,
                     @cat, @team, @tegen, @duur,
                     'Gepland', @door)
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

            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        public static async Task PopulateSunsetTableAsync(DateOnly from, DateOnly to)
        {
            using var conn = new SqlConnection(ConnectionString);
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

        // ── Herplan (reschedule) methods ──

        public static async Task<ZoekWedstrijdResponse?> FindMatchAsync(string teamNaam, DateOnly date)
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT TOP 1
                    CAST(m.[wedstrijdcode] AS BIGINT) AS Wedstrijdcode,
                    m.[wedstrijd],
                    CAST(m.[kaledatum] AS DATE) AS Datum,
                    m.[aanvangstijd],
                    COALESCE(CAST(md.[Duration] AS INT), s.[WedstrijdTotaal], 105) AS DuurMinuten,
                    m.[veld],
                    t.[leeftijdscategorie],
                    COALESCE(s.[Veldafmeting], 1.00) AS Veldafmeting
                FROM [his].[matches] m
                LEFT JOIN [his].[matchdetails] md ON CAST(md.[InternCode] AS BIGINT) = CAST(m.[wedstrijdcode] AS BIGINT)
                LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
                LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
                WHERE CAST(m.[kaledatum] AS DATE) = @date
                  AND m.[accommodatie] LIKE '%Spitsbergen%'
                  AND m.[status] <> 'Afgelast'
                  AND (m.[teamnaam] LIKE @teamPattern OR m.[wedstrijd] LIKE @teamPattern)
                ORDER BY m.[aanvangstijd]
            ", conn);
            cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("@teamPattern", $"%{teamNaam}%");

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var aanvangstijd = reader.GetString(3).Trim();
                var duur = reader.GetInt32(4);
                TimeOnly.TryParse(aanvangstijd, out var startTime);
                return new ZoekWedstrijdResponse
                {
                    Wedstrijdcode = reader.GetInt64(0),
                    Wedstrijd = reader.GetString(1).Trim(),
                    Datum = date.ToString("yyyy-MM-dd"),
                    AanvangsTijd = aanvangstijd,
                    EindTijd = startTime.AddMinutes(duur).ToString("HH:mm"),
                    DuurMinuten = duur,
                    VeldNaam = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                    LeeftijdsCategorie = reader.IsDBNull(6) ? null : reader.GetString(6).Trim(),
                    VeldDeelGebruik = reader.GetDecimal(7)
                };
            }
            return null;
        }

        public static async Task<ZoekWedstrijdResponse?> FindMatchByCodeAsync(long wedstrijdcode)
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT TOP 1
                    CAST(m.[wedstrijdcode] AS BIGINT) AS Wedstrijdcode,
                    m.[wedstrijd],
                    CAST(m.[kaledatum] AS DATE) AS Datum,
                    m.[aanvangstijd],
                    COALESCE(CAST(md.[Duration] AS INT), s.[WedstrijdTotaal], 105) AS DuurMinuten,
                    m.[veld],
                    t.[leeftijdscategorie],
                    COALESCE(s.[Veldafmeting], 1.00) AS Veldafmeting
                FROM [his].[matches] m
                LEFT JOIN [his].[matchdetails] md ON CAST(md.[InternCode] AS BIGINT) = CAST(m.[wedstrijdcode] AS BIGINT)
                LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
                LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
                WHERE CAST(m.[wedstrijdcode] AS BIGINT) = @code
                  AND m.[accommodatie] LIKE '%Spitsbergen%'
            ", conn);
            cmd.Parameters.AddWithValue("@code", wedstrijdcode);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var aanvangstijd = reader.GetString(3).Trim();
                var duur = reader.GetInt32(4);
                var datum = DateOnly.FromDateTime(reader.GetDateTime(2));
                TimeOnly.TryParse(aanvangstijd, out var startTime);
                return new ZoekWedstrijdResponse
                {
                    Wedstrijdcode = reader.GetInt64(0),
                    Wedstrijd = reader.GetString(1).Trim(),
                    Datum = datum.ToString("yyyy-MM-dd"),
                    AanvangsTijd = aanvangstijd,
                    EindTijd = startTime.AddMinutes(duur).ToString("HH:mm"),
                    DuurMinuten = duur,
                    VeldNaam = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                    LeeftijdsCategorie = reader.IsDBNull(6) ? null : reader.GetString(6).Trim(),
                    VeldDeelGebruik = reader.GetDecimal(7)
                };
            }
            return null;
        }

        public static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingAsync(
            DateOnly date, long excludeWedstrijdcode)
        {
            var all = await GetFieldOccupationsAsync(date);
            return all.Where(o => o.Wedstrijd == null ||
                !o.Wedstrijd.Contains(excludeWedstrijdcode.ToString())).ToList();
        }

        public static async Task<List<BestaandeWedstrijd>> GetFieldOccupationsExcludingMatchAsync(
            DateOnly date, string wedstrijdNaam, TimeOnly aanvangsTijd, int veldNummer)
        {
            var all = await GetFieldOccupationsAsync(date);
            return all.Where(o =>
                !(o.VeldNummer == veldNummer &&
                  o.AanvangsTijd == aanvangsTijd &&
                  o.Wedstrijd != null && o.Wedstrijd.Trim() == wedstrijdNaam.Trim())
            ).ToList();
        }

        public static async Task<int> SaveHerplanVerzoekAsync(
            long wedstrijdcode, string huidigeWedstrijd, DateOnly huidigeDatum,
            TimeOnly huidigeAanvangsTijd, string? huidigeVeldNaam,
            TimeOnly gewensteAanvangsTijd, int? gewenstVeldNummer,
            string? aangevraagdDoor, string? opmerking)
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                INSERT INTO [planner].[HerplanVerzoeken]
                    ([Wedstrijdcode], [HuidigeWedstrijd], [HuidigeDatum], [HuidigeAanvangsTijd],
                     [HuidigeVeldNaam], [GewensteAanvangsTijd], [GewenstVeldNummer],
                     [Status], [AangevraagdDoor], [Opmerking])
                OUTPUT INSERTED.[Id]
                VALUES
                    (@code, @wedstrijd, @datum, @aanvang,
                     @veld, @gewensteTijd, @gewenstVeld,
                     'Aangevraagd', @door, @opmerking)
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
    }
}
