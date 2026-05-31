using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner
{
    public static class PlannerDataAccess
    {
        private static string ConnectionString => SystemUtilities.DatabaseConfig.ConnectionString;

        public static async Task<Speeltijd?> GetSpeeltijdAsync(string leeftijdsCategorie, string? clubCode = null)
        {
            // clubCode is optioneel voor backward compat; valt terug op AppSettings (#428)
            var cc = clubCode ?? SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [Leeftijd], [Veldafmeting], [WedstrijdTotaal] FROM [dbo].[Speeltijden] WHERE [Leeftijd] = @cat AND [ClubCode] = @cc", conn);
            cmd.Parameters.AddWithValue("@cat", leeftijdsCategorie);
            cmd.Parameters.AddWithValue("@cc", cc);
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
                    ISNULL(s.[WedstrijdTotaal], 0) AS DuurMinuten,
                    v.[VeldNummer],
                    v.[VeldNaam],
                    m.[wedstrijd],
                    'Competitie' AS Bron
                FROM [his].[matches] m
                LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam]
                LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
                LEFT JOIN [dbo].[Velden] v ON RTRIM(LEFT(m.[veld], 6)) = v.[VeldNaam]
                WHERE CAST(m.[kaledatum] AS DATE) = @date
                  AND m.[status] <> 'Afgelast'
                  AND m.[teamnaam] = @exactTeamNaam

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
                    var wedstrijdNaam = reader.IsDBNull(5) ? "onbekend" : reader.GetString(5);
                    throw new InvalidOperationException($"Speelduur niet geconfigureerd voor wedstrijd '{wedstrijdNaam}'. Voeg de leeftijdscategorie toe aan dbo.Speeltijden via /instellingen/speeltijden.");
                }
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

        public static async Task<List<VeldBeschikbaarheidInfo>> GetAvailableFieldsAsync(DateOnly date, string? clubCode = null)
        {
            var results = new List<VeldBeschikbaarheidInfo>();
            // DayOfWeek: .NET Maandag=1 komt overeen met onze DB-conventie (1=Maandag...7=Zondag)
            int dagVanWeek = ((int)date.DayOfWeek == 0) ? 7 : (int)date.DayOfWeek;
            clubCode ??= SystemUtilities.AppSettings.GetSetting("clubCode") ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");

            using var conn = new SqlConnection(ConnectionString);
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

        public static async Task<List<VeldInfo>> GetVeldenAsync(string? clubCode = null)
        {
            clubCode ??= SystemUtilities.AppSettings.GetSetting("clubCode") ?? throw new InvalidOperationException("Vereiste instelling 'clubCode' ontbreekt in dbo.AppSettings");
            var results = new List<VeldInfo>();
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [VeldNummer], [VeldNaam], ISNULL([VeldType], 'kunstgras') AS [VeldType], [HeeftKunstlicht] FROM [dbo].[Velden] WHERE [Actief] = 1 AND [ClubCode] = @clubCode ORDER BY [VeldNummer]", conn);
            cmd.Parameters.AddWithValue("@clubCode", clubCode);
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
                    VeldSubpositie = reader.IsDBNull(8) ? null : reader.GetString(8)?.Trim(),
                    Bron = reader.GetString(9)
                });
            }
            return results;
        }

        // Lookup: VeldNaam (trimmed, 6 chars) → VeldNummer. Gebruikt door SportlinkApiClient.
        public static async Task<Dictionary<string, int>> GetVeldenLookupAsync()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [VeldNaam], [VeldNummer] FROM [dbo].[Velden] WHERE [Actief] = 1", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result[reader.GetString(0).TrimEnd()] = reader.GetInt32(1);
            return result;
        }

        // Lookup: Leeftijd → Speeltijd. Gebruikt door SportlinkApiClient.
        public static async Task<Dictionary<string, Speeltijd>> GetSpeeltijdenLookupAsync()
        {
            var result = new Dictionary<string, Speeltijd>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [Leeftijd], [Veldafmeting], [WedstrijdTotaal] FROM [dbo].[Speeltijden]", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result[reader.GetString(0)] = new Speeltijd
                {
                    Leeftijd       = reader.GetString(0),
                    Veldafmeting   = reader.GetDecimal(1),
                    WedstrijdTotaal = reader.GetInt32(2)
                };
            return result;
        }

        // Lookup: teamnaam → Speeltijden-sleutel (bijv. "JO13", "MO15", "G", "VR").
        // Gebruikt door SportlinkApiClient om /programma-records te mappen zonder SQL view.
        public static async Task<Dictionary<string, string>> GetTeamLeeftijdLookupAsync(string clubCode)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT [teamnaam],
                       REPLACE(REPLACE(REPLACE([leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
                FROM [his].[teams]
                WHERE [leeftijdscategorie] IS NOT NULL AND [leeftijdscategorie] <> ''", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var naam = reader.GetString(0);
                var key  = reader.GetString(1);
                result[naam] = key;
            }
            return result;
        }

        // Alleen planner-ingeplande wedstrijden (zonder his.matches). Gebruikt door SportlinkApiClient
        // om te combineren met real-time API-data.
        public static async Task<List<BestaandeWedstrijd>> GetGeplandeWedstrijdenOnlyAsync(DateOnly date)
        {
            var results = new List<BestaandeWedstrijd>();
            using var conn = new SqlConnection(ConnectionString);
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
            {
                results.Add(new BestaandeWedstrijd
                {
                    Datum             = DateOnly.FromDateTime(reader.GetDateTime(0)),
                    AanvangsTijd      = TimeOnly.FromTimeSpan(reader.GetTimeSpan(1)),
                    EindTijd          = TimeOnly.FromDateTime(reader.GetDateTime(2)),
                    VeldNummer        = reader.GetInt32(3),
                    VeldDeelGebruik   = reader.GetDecimal(4),
                    LeeftijdsCategorie = reader.IsDBNull(5) ? null : reader.GetString(5),
                    TeamNaam          = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Wedstrijd         = reader.IsDBNull(7) ? null : reader.GetString(7),
                    VeldSubpositie    = null,
                    Bron              = "Planner"
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

        public static async Task<Dictionary<string, (int bufferVoor, int bufferNa)>> GetAllTeamBuffersAsync()
        {
            var result = new Dictionary<string, (int bufferVoor, int bufferNa)>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                SELECT [TeamNaam], [RegelType], [WaardeMinuten]
                FROM [dbo].[TeamRegels]
                WHERE [RegelType] IN ('BufferVoor', 'BufferNa') AND [Actief] = 1 AND [WaardeMinuten] IS NOT NULL
            ", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var team = reader.GetString(0);
                var type = reader.GetString(1);
                var minuten = reader.GetInt32(2);
                if (!result.ContainsKey(team)) result[team] = (0, 0);
                var cur = result[team];
                result[team] = type == "BufferVoor"
                    ? (Math.Max(cur.bufferVoor, minuten), cur.bufferNa)
                    : (cur.bufferVoor, Math.Max(cur.bufferNa, minuten));
            }
            return result;
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
            string? tegenstander, int wedstrijdDuurMinuten, string? aangevraagdDoor,
            string? clubCode = null)
        {
            var cc = clubCode ?? SystemUtilities.AppSettings.GetSetting("clubCode") ?? "";
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                INSERT INTO [planner].[GeplandeWedstrijden]
                    ([Datum], [AanvangsTijd], [EindTijd], [VeldNummer], [VeldDeelGebruik],
                     [LeeftijdsCategorie], [TeamNaam], [Tegenstander], [WedstrijdDuurMinuten],
                     [Status], [AangevraagdDoor], [ClubCode])
                OUTPUT INSERTED.[Id]
                VALUES
                    (@datum, @aanvang, @eind, @veld, @deel,
                     @cat, @team, @tegen, @duur,
                     'Te bevestigen', @door, @cc)
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
                    ISNULL(s.[WedstrijdTotaal], 0) AS DuurMinuten,
                    m.[veld],
                    t.[leeftijdscategorie],
                    COALESCE(s.[Veldafmeting], 1.00) AS Veldafmeting
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
            cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{SystemUtilities.AppSettings.GetSetting("accommodatie") ?? throw new InvalidOperationException("Vereiste instelling 'accommodatie' ontbreekt in dbo.AppSettings")}%");

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var aanvangstijd = reader.GetString(3).Trim();
                var duur = reader.GetInt32(4);
                var wedstrijdNaamFind = reader.GetString(1).Trim();
                if (duur <= 0) throw new InvalidOperationException($"Speelduur niet geconfigureerd voor wedstrijd '{wedstrijdNaamFind}'. Voeg de leeftijdscategorie toe aan dbo.Speeltijden via /instellingen/speeltijden.");
                TimeOnly.TryParse(aanvangstijd, out var startTime);
                return new ZoekWedstrijdResponse
                {
                    Wedstrijdcode = reader.GetInt64(0),
                    Wedstrijd = wedstrijdNaamFind,
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

        /// <summary>
        /// Zoekt een wedstrijd op basis van de tegenstander-naam (fuzzy LIKE-match op wedstrijdnaam).
        /// Zoekt in his.matches (Sportlink-sync) én planner.GeplandeWedstrijden.
        /// Wanneer datum null is, worden alle toekomstige en recente wedstrijden doorzocht.
        /// </summary>
        public static async Task<ZoekWedstrijdResponse?> FindMatchByOpponentAsync(string tegenstander, DateOnly? datum)
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();

            // Zoek in his.matches (wedstrijdnaam bevat tegenstander)
            using (var cmd = new SqlCommand(@"
                SELECT TOP 1
                    CAST(m.[wedstrijdcode] AS BIGINT),
                    m.[wedstrijd],
                    CAST(m.[kaledatum] AS DATE),
                    m.[aanvangstijd],
                    ISNULL(s.[WedstrijdTotaal], 0),
                    m.[veld],
                    t.[leeftijdscategorie],
                    COALESCE(s.[Veldafmeting], 1.00)
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
                cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{SystemUtilities.AppSettings.GetSetting("accommodatie") ?? throw new InvalidOperationException("Vereiste instelling 'accommodatie' ontbreekt in dbo.AppSettings")}%");

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var aanvangstijd = reader.GetString(3).Trim();
                    var duur = reader.GetInt32(4);
                    var wedstrijdNaamOpp = reader.GetString(1).Trim();
                    if (duur <= 0) throw new InvalidOperationException($"Speelduur niet geconfigureerd voor wedstrijd '{wedstrijdNaamOpp}'. Voeg de leeftijdscategorie toe aan dbo.Speeltijden via /instellingen/speeltijden.");
                    var datumResult = DateOnly.FromDateTime(reader.GetDateTime(2));
                    TimeOnly.TryParse(aanvangstijd, out var startTime);
                    return new ZoekWedstrijdResponse
                    {
                        Wedstrijdcode = reader.GetInt64(0),
                        Wedstrijd = wedstrijdNaamOpp,
                        Datum = datumResult.ToString("yyyy-MM-dd"),
                        AanvangsTijd = aanvangstijd,
                        EindTijd = startTime.AddMinutes(duur).ToString("HH:mm"),
                        DuurMinuten = duur,
                        VeldNaam = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                        LeeftijdsCategorie = reader.IsDBNull(6) ? null : reader.GetString(6).Trim(),
                        VeldDeelGebruik = reader.GetDecimal(7)
                    };
                }
            }

            // Zoek in planner.GeplandeWedstrijden (Tegenstander-kolom)
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
                    TimeOnly.TryParse(aanvangstijd, out var startTime);
                    return new ZoekWedstrijdResponse
                    {
                        Wedstrijdcode = reader2.GetInt64(0),
                        Wedstrijd = reader2.GetString(1).Trim(),
                        Datum = datumResult.ToString("yyyy-MM-dd"),
                        AanvangsTijd = aanvangstijd,
                        EindTijd = startTime.AddMinutes(duur).ToString("HH:mm"),
                        DuurMinuten = duur,
                        VeldNaam = reader2.IsDBNull(5) ? null : reader2.GetString(5).Trim(),
                        LeeftijdsCategorie = reader2.IsDBNull(6) ? null : reader2.GetString(6).Trim(),
                        VeldDeelGebruik = reader2.GetDecimal(7)
                    };
                }
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
                    ISNULL(s.[WedstrijdTotaal], 0) AS DuurMinuten,
                    m.[veld],
                    t.[leeftijdscategorie],
                    COALESCE(s.[Veldafmeting], 1.00) AS Veldafmeting
                FROM [his].[matches] m
                LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
                LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = REPLACE(REPLACE(REPLACE(t.[leeftijdscategorie], 'Onder ', 'JO'), 'Meisjes ', 'MO'), 'Vrouwen', 'VR')
                WHERE CAST(m.[wedstrijdcode] AS BIGINT) = @code
                  AND m.[accommodatie] LIKE @accommodatiePattern
            ", conn);
            cmd.Parameters.AddWithValue("@code", wedstrijdcode);
            cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{SystemUtilities.AppSettings.GetSetting("accommodatie") ?? throw new InvalidOperationException("Vereiste instelling 'accommodatie' ontbreekt in dbo.AppSettings")}%");

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var aanvangstijd = reader.GetString(3).Trim();
                var duur = reader.GetInt32(4);
                var wedstrijdNaamCode = reader.GetString(1).Trim();
                if (duur <= 0) throw new InvalidOperationException($"Speelduur niet geconfigureerd voor wedstrijd '{wedstrijdNaamCode}'. Voeg de leeftijdscategorie toe aan dbo.Speeltijden via /instellingen/speeltijden.");
                var datum = DateOnly.FromDateTime(reader.GetDateTime(2));
                TimeOnly.TryParse(aanvangstijd, out var startTime);
                return new ZoekWedstrijdResponse
                {
                    Wedstrijdcode = reader.GetInt64(0),
                    Wedstrijd = wedstrijdNaamCode,
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

        /// <summary>
        /// Markeert geplande wedstrijden als vervallen zodra ze na de Sportlink-sync in his.matches staan.
        /// Match op Datum + TeamNaam — een team speelt maximaal 1 wedstrijd per dag.
        /// </summary>
        public static async Task MarkeerVervallenGeplandeWedstrijdenAsync(ILogger log)
        {
            var accommodatie = SystemUtilities.AppSettings.GetSetting("accommodatie");
            if (string.IsNullOrWhiteSpace(accommodatie))
            {
                log.LogWarning("Instelling 'accommodatie' niet geconfigureerd — MarkeerVervallenGeplandeWedstrijden overgeslagen. Stel de accommodatienaam in via Admin GUI → Instellingen.");
                return;
            }
            using var conn = new SqlConnection(ConnectionString);
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
                log.LogInformation("Post-sync: {Count} geplande wedstrijd(en) als vervallen gemarkeerd (overgenomen in Sportlink)", rows);
        }

        public static async Task<DateOnly?> GetSeasonEndDateAsync()
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT MAX(DateUntil) FROM [dbo].[Season]", conn);
            var result = await cmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
                return DateOnly.FromDateTime(Convert.ToDateTime(result));
            return null;
        }

        public static async Task<DateTime?> GetLastSyncTimestampAsync()
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [LastSyncTimestamp] FROM [dbo].[AppSettings]", conn);
            var result = await cmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
                return Convert.ToDateTime(result);
            return null;
        }

        // ── Team-schedule (#70) ──

        public static async Task<bool> TeamExistsAsync(string team)
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT COUNT(1) FROM [his].[teams] WHERE UPPER([teamnaam]) = UPPER(@team)", conn);
            cmd.Parameters.AddWithValue("@team", team);
            var count = (int)(await cmd.ExecuteScalarAsync())!;
            return count > 0;
        }

        public static async Task<List<TeamScheduleWedstrijd>> GetFutureMatchesForTeamAsync(
            string team, DateOnly van, DateOnly tot)
        {
            var results = new List<TeamScheduleWedstrijd>();
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();

            // his.matches: competitie- en bekerwedstrijden (thuis en uit)
            using (var cmd = new SqlCommand(@"
                SELECT
                    CAST(m.[kaledatum] AS DATE) AS Datum,
                    m.[aanvangstijd],
                    m.[thuisteam],
                    m.[uitteam],
                    m.[competitiesoort],
                    m.[veld],
                    CAST(m.[wedstrijdcode] AS BIGINT) AS Wedstrijdcode
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
                    var competitiesoort = reader.IsDBNull(4) ? "" : reader.GetString(4).Trim();
                    var veld = reader.IsDBNull(5) ? null : reader.GetString(5).Trim();
                    var wedstrijdcode = reader.GetInt64(6);

                    bool isThuis = thuisTeam.Equals(team, StringComparison.OrdinalIgnoreCase);
                    var tegenstander = isThuis ? uitTeam : thuisTeam;
                    var type = DetermineMatchType(competitiesoort);

                    results.Add(new TeamScheduleWedstrijd
                    {
                        Datum = datum.ToString("yyyy-MM-dd"),
                        AanvangsTijd = aanvang,
                        ThuisUit = isThuis ? "thuis" : "uit",
                        Tegenstander = tegenstander,
                        Type = type,
                        Veld = veld,
                        Wedstrijdcode = wedstrijdcode
                    });
                }
            }

            // planner.GeplandeWedstrijden: geplande oefenwedstrijden (altijd thuis)
            using (var cmd2 = new SqlCommand(@"
                SELECT
                    gw.[Datum],
                    CONVERT(VARCHAR(8), gw.[AanvangsTijd], 108) AS AanvangsTijd,
                    gw.[Tegenstander],
                    v.[VeldNaam]
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
                {
                    var datum = DateOnly.FromDateTime(reader2.GetDateTime(0));
                    results.Add(new TeamScheduleWedstrijd
                    {
                        Datum = datum.ToString("yyyy-MM-dd"),
                        AanvangsTijd = reader2.GetString(1),
                        ThuisUit = "thuis",
                        Tegenstander = reader2.IsDBNull(2) ? "" : reader2.GetString(2),
                        Type = "oefenwedstrijd",
                        Veld = reader2.IsDBNull(3) ? null : reader2.GetString(3),
                        Wedstrijdcode = null
                    });
                }
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

        // ── Auto-plan data access (#380) ──

        /// <summary>
        /// Laadt voorkeurstijden als lookup: TeamNaam → gesorteerde lijst van (Tijd, Prioriteit).
        /// Lagere Prioriteit-waarde = hogere voorkeur (1=hoogst). Filtert op dag + clubCode + Actief=1.
        /// DagVanWeek: 1=Maandag … 6=Zaterdag, 7=Zondag.
        /// </summary>
        public static async Task<Dictionary<string, List<(TimeOnly Tijd, int Prioriteit)>>> GetVoorkeurTijdenLookupAsync(int dagVanWeek, string clubCode)
        {
            var result = new Dictionary<string, List<(TimeOnly, int)>>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqlConnection(ConnectionString);
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
                if (!result.ContainsKey(team))
                    result[team] = new List<(TimeOnly, int)>();
                result[team].Add((tijd, prio));
            }
            return result;
        }

        // ALLSTARS testmodus: velden met VeldNummer >= 100 (testmodus-velden)
        public static async Task<List<VeldInfo>> GetAllstarsVeldenAsync()
        {
            var results = new List<VeldInfo>();
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT [VeldNummer], [VeldNaam], ISNULL([VeldType], 'kunstgras') AS [VeldType], [HeeftKunstlicht] FROM [dbo].[Velden] WHERE [Actief] = 1 AND [VeldNummer] >= 100 ORDER BY [VeldNummer]", conn);
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

        /// <summary>
        /// Haalt ALLE wedstrijden voor een datum op, inclusief die zonder veld of aanvangstijd.
        /// Voor ALLSTARS: filtert op ClubCode='ALLSTARS'.
        /// Voor echte clubs: filtert op eigen accommodatie uit dbo.AppSettings.
        /// </summary>
        public static async Task<List<WedstrijdRaw>> GetAllMatchesForDatumAsync(DateOnly datum, string clubCode)
        {
            bool isAllstars = clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase);
            var results = new List<WedstrijdRaw>();
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();

            string sql = isAllstars
                ? @"
                    SELECT m.[wedstrijdcode],
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
                : @"
                    SELECT m.[wedstrijdcode], m.[wedstrijd], m.[teamnaam], m.[uitteam],
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
            {
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
            }
            return results;
        }

        /// <summary>
        /// Past aanvangstijd en veld aan in his.matches voor ALLSTARS testdata.
        /// Alleen toegestaan voor ClubCode='ALLSTARS' — productiedata kan niet via de API worden gewijzigd.
        /// </summary>
        public static async Task<int> UpdateAllstarsMatchAsync(long wedstrijdCode, string nieuweVeld, string nieuweTijd)
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
                UPDATE [his].[matches]
                SET [aanvangstijd] = @tijd,
                    [veld] = @veld,
                    [mta_modified] = GETUTCDATE()
                WHERE [wedstrijdcode] = @code
                  AND [ClubCode] = 'ALLSTARS'
            ", conn);
            cmd.Parameters.AddWithValue("@tijd", nieuweTijd);
            cmd.Parameters.AddWithValue("@veld", nieuweVeld);
            cmd.Parameters.AddWithValue("@code", wedstrijdCode);
            return await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Zoekt de primaire teamleider/trainer/coach voor een team in avg.Teambegeleiding.
        /// AVG: het resultaat bevat persoonsgegevens — gebruik alleen voor interne notificaties.
        /// </summary>
        public static async Task<TeamleiderContact?> GetTeamleiderContactAsync(string teamNaam)
        {
            // Strip club-prefix voor flexibele match (bijv. "VRC JO11-1" → "JO11-1" als fallback)
            var parts = teamNaam.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var teamZonderPrefix = parts.Length > 1 ? parts[1] : teamNaam;

            using var conn = new SqlConnection(ConnectionString);
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
            {
                return new TeamleiderContact
                {
                    Naam = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Emailadres = reader.IsDBNull(1) ? "" : reader.GetString(1)
                };
            }
            return null;
        }
    }
}
