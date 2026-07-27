using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner;

/// <summary>
/// Repository voor wedstrijd-opzoeken, plannen, herplannen en vervallen markeren.
/// Extracted uit PlannerDataAccess (#474).
///
/// Alle leesquery's zijn hard gescoped op ClubCode (#573) — zie <see cref="ClubScope"/>.
/// </summary>
internal static class PlannerMatchRepository
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    /// <summary>
    /// Alle schrijfwijzen waarmee een team in de brondata voorkomt (#700).
    ///
    /// <para>
    /// De teamnaam wordt eerst genormaliseerd (zie <see cref="TeamResolution.TeamNaamNormalisatie"/>) en
    /// via <c>dbo.Teams</c>/<c>dbo.TeamAliassen</c> herleid tot één team; daarna worden álle bekende
    /// schrijfwijzen van dat team teruggegeven. Vergelijken in de query gebeurt vervolgens met
    /// <b>gelijkheid</b> in plaats van met een <c>LIKE</c>-patroon.
    /// </para>
    /// <para>
    /// Dit is nodig omdat de schrijfwijze per bron verschilt: <c>his.matches</c> gebruikt
    /// "[club] JO10-1" (mét J), de bondsrijen in <c>his.teams</c> "[club] O10-1" (zonder), en de
    /// e-mailclassificatie levert weer een derde vorm. Het herleiden gebeurt hier in de repository, zodat
    /// elke aanroeper met elke schrijfwijze terechtkomt bij hetzelfde team.
    /// </para>
    /// <para>
    /// Een lege uitkomst betekent: dit is geen bekend team van deze club. De aanroepende query moet dan
    /// niets matchen — nooit alles.
    /// </para>
    /// </summary>
    private static async Task<List<string>> TeamSchrijfwijzenAsync(
        SqlConnection conn, string clubCode, string? teamNaam)
    {
        var resultaten = new List<string>();
        var sleutel = TeamResolution.TeamNaamNormalisatie.NormaliseerVoorVergelijking(teamNaam, clubCode);
        if (sleutel.Length == 0) return resultaten;

        using var cmd = new SqlCommand(@"
            DECLARE @teamId INT = COALESCE(
                (SELECT TOP 1 [TeamId] FROM [dbo].[Teams]
                 WHERE [ClubCode] = @clubCode AND [TeamnaamGenormaliseerd] = @sleutel AND [IsActief] = 1),
                (SELECT TOP 1 a.[TeamId] FROM [dbo].[TeamAliassen] a
                 INNER JOIN [dbo].[Teams] t ON t.[TeamId] = a.[TeamId] AND t.[IsActief] = 1
                 WHERE a.[ClubCode] = @clubCode AND a.[Status] = 'validated'
                   AND (a.[RuweTekst] = @ruweTekst OR a.[RuweTekstGenormaliseerd] = @sleutel)));

            IF @teamId IS NULL RETURN;

            SELECT [Teamnaam] FROM [dbo].[Teams] WHERE [TeamId] = @teamId
            UNION
            SELECT [RuweTekst] FROM [dbo].[TeamAliassen]
            WHERE [ClubCode] = @clubCode AND [TeamId] = @teamId AND [Status] = 'validated';
        ", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        cmd.Parameters.AddWithValue("@sleutel", sleutel);
        cmd.Parameters.AddWithValue("@ruweTekst", (teamNaam ?? "").Trim());

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0)) resultaten.Add(reader.GetString(0));
        }
        return resultaten;
    }

    /// <summary>
    /// Geparameteriseerde <c>IN (...)</c>-lijst. Bij een lege lijst <c>1 = 0</c>, zodat de query niets
    /// matcht in plaats van alles.
    /// </summary>
    private static string BouwSchrijfwijzenFilter(
        SqlCommand cmd, string kolom, IReadOnlyList<string> schrijfwijzen, string parameterPrefix)
    {
        if (schrijfwijzen.Count == 0) return "1 = 0";

        var namen = new List<string>(schrijfwijzen.Count);
        for (int i = 0; i < schrijfwijzen.Count; i++)
        {
            var naam = $"@{parameterPrefix}{i}";
            cmd.Parameters.AddWithValue(naam, schrijfwijzen[i]);
            namen.Add(naam);
        }
        return $"{kolom} IN ({string.Join(", ", namen)})";
    }

    /// <remarks>
    /// Detecteert of hetzelfde team die dag al speelt. Sinds #700 wordt daarvoor exact vergeleken met
    /// alle bekende schrijfwijzen van het team: een gemiste vergelijking hier zou stilzwijgend een
    /// dubbele boeking van hetzelfde team toelaten.
    /// </remarks>
    internal static async Task<List<BestaandeWedstrijd>> GetTeamMatchesOnDateAsync(
        string teamNaam, DateOnly date, string? clubCode = null)
    {
        var results = new List<BestaandeWedstrijd>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        var cc = ClubScope.Resolve(clubCode);
        var schrijfwijzen = await TeamSchrijfwijzenAsync(conn, cc, teamNaam);
        if (schrijfwijzen.Count == 0) return results;

        using var cmd = new SqlCommand();
        var matchFilter = BouwSchrijfwijzenFilter(cmd, "m.[teamnaam]", schrijfwijzen, "team");
        var plannerFilter = BouwSchrijfwijzenFilter(cmd, "gw.[TeamNaam]", schrijfwijzen, "gwteam");
        cmd.Connection = conn;
        cmd.CommandText = $@"
            SELECT
                CAST(m.[kaledatum] AS DATE) AS Datum,
                CAST(m.[aanvangstijd] AS TIME) AS AanvangsTijd,
                ISNULL(s.[WedstrijdTotaal], 0) AS DuurMinuten,
                v.[VeldNummer], v.[VeldNaam], m.[wedstrijd], 'Competitie' AS Bron
            FROM [his].[matches] m
            LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam]
                 AND {ClubScope.HisFilter("t")}
            LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = {LeeftijdNormalisatie.SqlExpr("t.[leeftijdscategorie]")}
                 AND s.[ClubCode] = {ClubScope.ClubCodeParam}
            LEFT JOIN [dbo].[Velden] v ON RTRIM(LEFT(m.[veld], 6)) = v.[VeldNaam]
                 AND v.[ClubCode] = {ClubScope.ClubCodeParam}
            WHERE CAST(m.[kaledatum] AS DATE) = @date
              AND m.[status] <> 'Afgelast'
              AND {matchFilter}
              AND {ClubScope.HisFilter("m")}
            UNION ALL
            SELECT gw.[Datum], gw.[AanvangsTijd], gw.[WedstrijdDuurMinuten],
                   gw.[VeldNummer], v.[VeldNaam],
                   COALESCE(gw.[TeamNaam], '') + ' - ' + COALESCE(gw.[Tegenstander], ''), 'Planner'
            FROM [planner].[GeplandeWedstrijden] gw
            LEFT JOIN [dbo].[Velden] v ON v.[VeldNummer] = gw.[VeldNummer]
                 AND v.[ClubCode] = {ClubScope.ClubCodeParam}
            WHERE gw.[Datum] = @date
              AND gw.[Status] <> 'Geannuleerd'
              AND {plannerFilter}
              AND gw.[ClubCode] = {ClubScope.ClubCodeParam}
        ";
        cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        ClubScope.AddHisParams(cmd, clubCode);
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

    internal static async Task<List<BestaandeWedstrijd>> GetGeplandeWedstrijdenOnlyAsync(
        DateOnly date, string? clubCode = null)
    {
        var results = new List<BestaandeWedstrijd>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT gw.[Datum], gw.[AanvangsTijd], gw.[EindTijd],
                   gw.[VeldNummer], gw.[VeldDeelGebruik], gw.[LeeftijdsCategorie],
                   gw.[TeamNaam],
                   COALESCE(gw.[TeamNaam], '') + ' - ' + COALESCE(gw.[Tegenstander], '') AS Wedstrijd,
                   v.[VeldNaam], 'Planner' AS Bron, gw.[SportlinkWedstrijdCode]
            FROM [planner].[GeplandeWedstrijden] gw
            LEFT JOIN [dbo].[Velden] v ON v.[VeldNummer] = gw.[VeldNummer]
                 AND v.[ClubCode] = {ClubScope.ClubCodeParam}
            WHERE gw.[Datum] = @date
              AND gw.[Status] <> 'Geannuleerd'
              AND gw.[IsVervallen] = 0
              AND gw.[ClubCode] = {ClubScope.ClubCodeParam}", conn);
        cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        ClubScope.AddClubParam(cmd, clubCode);
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
                Bron               = "Planner",
                Wedstrijdcode      = reader.IsDBNull(10) ? null : reader.GetInt64(10)
            });
        return results;
    }

    /// <remarks>
    /// Sinds #700 wordt <paramref name="teamNaam"/> eerst herleid tot een bekend team en daarna exact
    /// vergeleken met alle schrijfwijzen van dat team. Er is geen terugval op een <c>LIKE</c>-patroon:
    /// een onbekende teamnaam levert géén wedstrijd op, in plaats van mogelijk de verkeerde.
    /// </remarks>
    internal static async Task<ZoekWedstrijdResponse?> FindMatchAsync(
        string teamNaam, DateOnly date, string? clubCode = null)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        var cc = ClubScope.Resolve(clubCode);
        var accommodatie = await ClubScope.RequireAccommodatieAsync(conn, cc);

        var schrijfwijzen = await TeamSchrijfwijzenAsync(conn, cc, teamNaam);
        if (schrijfwijzen.Count == 0) return null;

        using var cmd = new SqlCommand();
        var teamFilter = BouwSchrijfwijzenFilter(cmd, "m.[teamnaam]", schrijfwijzen, "team");

        cmd.Connection = conn;
        cmd.CommandText = $@"
            SELECT TOP 1
                CAST(m.[wedstrijdcode] AS BIGINT), m.[wedstrijd],
                CAST(m.[kaledatum] AS DATE), m.[aanvangstijd],
                ISNULL(s.[WedstrijdTotaal], 0), m.[veld],
                t.[leeftijdscategorie], COALESCE(s.[Veldafmeting], 1.00)
            FROM [his].[matches] m
            LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
                 AND {ClubScope.HisFilter("t")}
            LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = {LeeftijdNormalisatie.SqlExpr("t.[leeftijdscategorie]")}
                 AND s.[ClubCode] = {ClubScope.ClubCodeParam}
            WHERE CAST(m.[kaledatum] AS DATE) = @date
              AND m.[accommodatie] LIKE @accommodatiePattern
              AND m.[status] <> 'Afgelast'
              AND {teamFilter}
              AND {ClubScope.HisFilter("m")}
            ORDER BY m.[aanvangstijd]
        ";
        cmd.Parameters.AddWithValue("@date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{accommodatie}%");
        ClubScope.AddHisParams(cmd, clubCode);
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

    internal static async Task<ZoekWedstrijdResponse?> FindMatchByOpponentAsync(
        string tegenstander, DateOnly? datum, string? clubCode = null)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        var accommodatie = await ClubScope.RequireAccommodatieAsync(conn, ClubScope.Resolve(clubCode));

        // Zoek in his.matches
        using (var cmd = new SqlCommand($@"
            SELECT TOP 1
                CAST(m.[wedstrijdcode] AS BIGINT), m.[wedstrijd],
                CAST(m.[kaledatum] AS DATE), m.[aanvangstijd],
                ISNULL(s.[WedstrijdTotaal], 0), m.[veld],
                t.[leeftijdscategorie], COALESCE(s.[Veldafmeting], 1.00)
            FROM [his].[matches] m
            LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
                 AND {ClubScope.HisFilter("t")}
            LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = {LeeftijdNormalisatie.SqlExpr("t.[leeftijdscategorie]")}
                 AND s.[ClubCode] = {ClubScope.ClubCodeParam}
            WHERE m.[accommodatie] LIKE @accommodatiePattern
              AND m.[status] <> 'Afgelast'
              AND m.[wedstrijd] LIKE @tegPattern
              AND (@datum IS NULL OR CAST(m.[kaledatum] AS DATE) = @datum)
              AND {ClubScope.HisFilter("m")}
            ORDER BY m.[kaledatum]
        ", conn))
        {
            cmd.Parameters.AddWithValue("@tegPattern", $"%{tegenstander}%");
            cmd.Parameters.Add("@datum", System.Data.SqlDbType.Date).Value =
                datum.HasValue ? datum.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
            cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{accommodatie}%");
            ClubScope.AddHisParams(cmd, clubCode);
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
        using (var cmd2 = new SqlCommand($@"
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
                 AND v.[ClubCode] = {ClubScope.ClubCodeParam}
            WHERE gw.[Status] <> 'Geannuleerd'
              AND gw.[Tegenstander] LIKE @tegPattern
              AND (@datum IS NULL OR gw.[Datum] = @datum)
              AND gw.[ClubCode] = {ClubScope.ClubCodeParam}
            ORDER BY gw.[Datum]
        ", conn))
        {
            cmd2.Parameters.AddWithValue("@tegPattern", $"%{tegenstander}%");
            cmd2.Parameters.Add("@datum", System.Data.SqlDbType.Date).Value =
                datum.HasValue ? datum.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
            ClubScope.AddClubParam(cmd2, clubCode);
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

    internal static async Task<ZoekWedstrijdResponse?> FindMatchByCodeAsync(
        long wedstrijdcode, string? clubCode = null)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        var accommodatie = await ClubScope.RequireAccommodatieAsync(conn, ClubScope.Resolve(clubCode));
        using var cmd = new SqlCommand($@"
            SELECT TOP 1
                CAST(m.[wedstrijdcode] AS BIGINT), m.[wedstrijd],
                CAST(m.[kaledatum] AS DATE), m.[aanvangstijd],
                ISNULL(s.[WedstrijdTotaal], 0), m.[veld],
                t.[leeftijdscategorie], COALESCE(s.[Veldafmeting], 1.00)
            FROM [his].[matches] m
            LEFT JOIN [his].[teams] t ON t.[teamnaam] = m.[teamnaam] AND t.[leeftijdscategorie] IS NOT NULL AND t.[leeftijdscategorie] <> ''
                 AND {ClubScope.HisFilter("t")}
            LEFT JOIN [dbo].[Speeltijden] s ON s.[Leeftijd] = {LeeftijdNormalisatie.SqlExpr("t.[leeftijdscategorie]")}
                 AND s.[ClubCode] = {ClubScope.ClubCodeParam}
            WHERE CAST(m.[wedstrijdcode] AS BIGINT) = @code
              AND m.[accommodatie] LIKE @accommodatiePattern
              AND {ClubScope.HisFilter("m")}
        ", conn);
        cmd.Parameters.AddWithValue("@code", wedstrijdcode);
        cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{accommodatie}%");
        ClubScope.AddHisParams(cmd, clubCode);
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
        var cc = SystemUtilities.AppSettings.RequireClubCode(clubCode);
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

    internal static async Task MarkeerVervallenGeplandeWedstrijdenAsync(ILogger log, string? clubCode = null)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        string accommodatie;
        try
        {
            accommodatie = await ClubScope.RequireAccommodatieAsync(conn, ClubScope.Resolve(clubCode));
        }
        catch (InvalidOperationException)
        {
            log.LogWarning("Instelling 'Accommodatie' niet geconfigureerd — MarkeerVervallenGeplandeWedstrijden overgeslagen. Stel de accommodatienaam in via Admin GUI → Instellingen.");
            return;
        }
        // De teamnaam die wij in planner.GeplandeWedstrijden schrijven en die in his.matches komen uit
        // verschillende naamconventies ("[club] O13-1" versus "[club] JO13-1"). Een directe
        // stringvergelijking tussen die twee kolommen matcht daarom nooit, waardoor een handmatig
        // ingeplande wedstrijd niet als vervallen werd gemarkeerd zodra de KNVB hem publiceerde.
        // Beide kanten worden nu via de aliassen naar hetzelfde team herleid (#700).
        using var cmd = new SqlCommand($@"
            UPDATE gw
            SET gw.[IsVervallen] = 1,
                gw.[SportlinkWedstrijdCode] = CAST(m.[wedstrijdcode] AS BIGINT),
                gw.[mta_modified] = GETUTCDATE()
            FROM [planner].[GeplandeWedstrijden] gw
            INNER JOIN [his].[matches] m
                ON CAST(m.[kaledatum] AS DATE) = gw.[Datum]
                AND {ClubScope.HisFilter("m")}
                AND EXISTS (
                    SELECT 1
                    FROM [dbo].[TeamAliassen] aMatch
                    INNER JOIN [dbo].[TeamAliassen] aPlanner
                        ON aPlanner.[TeamId] = aMatch.[TeamId]
                       AND aPlanner.[ClubCode] = aMatch.[ClubCode]
                       AND aPlanner.[Status] = 'validated'
                       AND aPlanner.[RuweTekst] = gw.[TeamNaam]
                    WHERE aMatch.[ClubCode] = {ClubScope.ClubCodeParam}
                      AND aMatch.[Status] = 'validated'
                      AND aMatch.[RuweTekst] = m.[teamnaam])
            WHERE gw.[IsVervallen] = 0
              AND gw.[Status] <> 'Geannuleerd'
              AND gw.[ClubCode] = {ClubScope.ClubCodeParam}
              AND m.[accommodatie] LIKE @accommodatiePattern
        ", conn);
        cmd.Parameters.AddWithValue("@accommodatiePattern", $"%{accommodatie}%");
        ClubScope.AddHisParams(cmd, clubCode);
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows > 0)
            log.LogInformation("Post-sync: {Count} geplande wedstrijd(en) als vervallen gemarkeerd", rows);
    }

    /// <remarks>
    /// Vraagt de canonieke teamlijst, niet <c>his.teams</c> rechtstreeks: die laatste bevat elk team in
    /// meerdere schrijfwijzen, waardoor een vergelijking op de ruwe naam afhankelijk werd van welke
    /// notatie de aanroeper toevallig gebruikte (#700).
    /// </remarks>
    internal static async Task<bool> TeamExistsAsync(string team, string? clubCode = null)
    {
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        var cc = ClubScope.Resolve(clubCode);
        return (await TeamSchrijfwijzenAsync(conn, cc, team)).Count > 0;
    }

    internal static async Task<List<TeamScheduleWedstrijd>> GetFutureMatchesForTeamAsync(
        string team, DateOnly van, DateOnly tot, string? clubCode = null)
    {
        var results = new List<TeamScheduleWedstrijd>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        var cc = ClubScope.Resolve(clubCode);
        var schrijfwijzen = await TeamSchrijfwijzenAsync(conn, cc, team);
        if (schrijfwijzen.Count == 0) return results;

        using (var cmd = new SqlCommand())
        {
            var matchFilter = BouwSchrijfwijzenFilter(cmd, "m.[teamnaam]", schrijfwijzen, "team");
            cmd.Connection = conn;
            cmd.CommandText = $@"
            SELECT CAST(m.[kaledatum] AS DATE), m.[aanvangstijd],
                   m.[thuisteam], m.[uitteam], m.[competitiesoort], m.[veld],
                   CAST(m.[wedstrijdcode] AS BIGINT)
            FROM [his].[matches] m
            WHERE CAST(m.[kaledatum] AS DATE) BETWEEN @van AND @tot
              AND m.[status] <> 'Afgelast'
              AND {matchFilter}
              AND {ClubScope.HisFilter("m")}
            ORDER BY m.[kaledatum], m.[aanvangstijd]
        ";
            cmd.Parameters.AddWithValue("@van", van.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue("@tot", tot.ToDateTime(TimeOnly.MinValue));
            ClubScope.AddHisParams(cmd, clubCode);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var datum = DateOnly.FromDateTime(reader.GetDateTime(0));
                var aanvang = reader.GetString(1).Trim();
                var thuisTeam = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
                var uitTeam = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim();
                bool isThuis = schrijfwijzen.Any(w => thuisTeam.Equals(w, StringComparison.OrdinalIgnoreCase));
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

        using (var cmd2 = new SqlCommand())
        {
            var plannerFilter = BouwSchrijfwijzenFilter(cmd2, "gw.[TeamNaam]", schrijfwijzen, "gwteam");
            cmd2.Connection = conn;
            cmd2.CommandText = $@"
            SELECT gw.[Datum], CONVERT(VARCHAR(8), gw.[AanvangsTijd], 108),
                   gw.[Tegenstander], v.[VeldNaam]
            FROM [planner].[GeplandeWedstrijden] gw
            LEFT JOIN [dbo].[Velden] v ON v.[VeldNummer] = gw.[VeldNummer]
                 AND v.[ClubCode] = {ClubScope.ClubCodeParam}
            WHERE gw.[Datum] BETWEEN @van AND @tot
              AND gw.[Status] <> 'Geannuleerd'
              AND {plannerFilter}
              AND gw.[ClubCode] = {ClubScope.ClubCodeParam}
            ORDER BY gw.[Datum], gw.[AanvangsTijd]
        ";
            cmd2.Parameters.AddWithValue("@van", van.ToDateTime(TimeOnly.MinValue));
            cmd2.Parameters.AddWithValue("@tot", tot.ToDateTime(TimeOnly.MinValue));
            ClubScope.AddClubParam(cmd2, clubCode);
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
