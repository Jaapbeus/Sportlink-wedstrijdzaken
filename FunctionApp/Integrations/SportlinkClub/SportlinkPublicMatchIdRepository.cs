using Microsoft.Data.SqlClient;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Planner;

namespace SportlinkFunction.Integrations.SportlinkClub;

/// <summary>
/// SQL Server-tegenhanger van
/// <c>FunctionApp.Postgres/Integrations/SportlinkClub/SportlinkPublicMatchIdRepository.cs</c>
/// (#1266, epic #986): databasetoegang voor de PublicMatchId-cache (#991). Cachet het resultaat van
/// de trage (12+ s), niet-club-gescoped reverse-lookup in <c>dbo.SportlinkPublicMatchIdCache</c> —
/// <b>niet</b> als kolom op <c>his.matches</c>, want die tabel wordt bij een schemawijziging van de
/// Sportlink-staging volledig opnieuw opgebouwd (zie <c>Database/his/Tables/Matches.sql</c>); een
/// eigen cache-kolom daarin zou onze cache-data vermengen met Sportlink-gesynchroniseerde data én
/// bij de volgende herbouw verdwijnen.
/// <para>
/// Bewust een eigen, parallelle implementatie naast de Postgres-versie (geen runtime-
/// providerabstractie, docs/ARCHITECTUUR-DATABASE-TIERS.md). Wat wél gedeeld is, is de vorm van de
/// gegevens: <see cref="WedstrijdVoorLookup"/>, <see cref="WedstrijdZonderCache"/> en
/// <see cref="SportlinkWedstrijdContext"/> staan in Planner.Shared.
/// </para>
/// <para>
/// <b>ClubCode-scope.</b> <c>his.matches.ClubCode</c> is nullable (rijen van vóór migratie 001
/// horen bij de primaire club) — daarom <see cref="ClubScope.HisFilter"/> en niet een strikte
/// gelijkheid zoals in de Postgres-tier, waar de kolom wél altijd gevuld is.
/// <c>dbo.SportlinkPublicMatchIdCache.ClubCode</c> is NOT NULL en wordt dus wél strikt gefilterd.
/// </para>
/// </summary>
internal static class SportlinkPublicMatchIdRepository
{
    /// <summary>Foutnummer van SQL Server voor "Invalid object name" — <c>his.matches</c> bestaat
    /// pas na de eerste sync (de tabel wordt dynamisch aangemaakt).</summary>
    private const int SqlFoutOngeldigeObjectnaam = 208;

    /// <summary>Zoekt <c>wedstrijdnummer</c>/<c>kaledatum</c> op via onze eigen <c>wedstrijdcode</c>
    /// (issue #991's externe sleutel). <c>kaledatum</c> i.p.v. <c>wedstrijddatum</c>: dat laatste is
    /// een vrije Sportlink-weergavestring, <c>kaledatum</c> is een parseerbare datum (zelfde keuze
    /// als <c>PlannerMatchRepository</c>).</summary>
    internal static async Task<WedstrijdVoorLookup?> ZoekWedstrijdAsync(
        SqlConnection connection, long wedstrijdcode, string clubCode)
    {
        using var cmd = new SqlCommand($@"
            SELECT TOP 1 m.[wedstrijdnummer], CAST(m.[kaledatum] AS DATE)
            FROM [his].[matches] m
            WHERE m.[wedstrijdcode] = @Wedstrijdcode
              AND {ClubScope.HisFilter("m")}
              AND m.[mta_deleted] IS NULL", connection);
        cmd.Parameters.AddWithValue("@Wedstrijdcode", wedstrijdcode);
        ClubScope.AddHisParams(cmd, clubCode);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        // wedstrijdnummer/kaledatum kunnen NULL zijn op een onvolledig gesynchroniseerde rij —
        // dan is de reverse-lookup niet mogelijk, geen crash.
        if (reader.IsDBNull(0) || reader.IsDBNull(1)) return null;

        return new WedstrijdVoorLookup(reader.GetInt64(0), DateOnly.FromDateTime(reader.GetDateTime(1)));
    }

    /// <summary>Alle eigen wedstrijden binnen <paramref name="vanaf"/>–<paramref name="totEnMet"/>
    /// (inclusief) die nog geen <c>PublicMatchId</c> in de cache hebben — gebruikt door de
    /// #1017-warmup-timer om te bepalen wat er nog opgehaald moet worden vóórdat een gebruiker er
    /// zelf naar vraagt.</summary>
    internal static async Task<List<WedstrijdZonderCache>> ZoekWedstrijdenZonderCacheAsync(
        SqlConnection connection, DateOnly vanaf, DateOnly totEnMet, string clubCode)
    {
        var resultaat = new List<WedstrijdZonderCache>();
        using var cmd = new SqlCommand($@"
            SELECT m.[wedstrijdcode], m.[wedstrijdnummer], CAST(m.[kaledatum] AS DATE)
            FROM [his].[matches] m
            LEFT JOIN [dbo].[SportlinkPublicMatchIdCache] c
                ON c.[Wedstrijdcode] = m.[wedstrijdcode]
               AND c.[ClubCode] = {ClubScope.ClubCodeParam}
            WHERE {ClubScope.HisFilter("m")}
              AND CAST(m.[kaledatum] AS DATE) BETWEEN @Vanaf AND @TotEnMet
              AND m.[wedstrijdnummer] IS NOT NULL
              AND m.[mta_deleted] IS NULL
              AND c.[Wedstrijdcode] IS NULL", connection);
        ClubScope.AddHisParams(cmd, clubCode);
        cmd.Parameters.AddWithValue("@Vanaf", vanaf.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("@TotEnMet", totEnMet.ToDateTime(TimeOnly.MinValue));

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            resultaat.Add(new WedstrijdZonderCache(
                reader.GetInt64(0),
                reader.GetInt64(1),
                DateOnly.FromDateTime(reader.GetDateTime(2))));
        }
        return resultaat;
    }

    /// <summary>
    /// #1111: de omgekeerde richting van de cache — van Sportlinks <c>PublicMatchId</c> naar onze
    /// eigen wedstrijd in <c>his.matches</c>, in één query voor alle verzoeken van
    /// <c>/wijzigingsverzoeken</c>. Alleen wedstrijden die al in de cache staan (warmup-timer #1017
    /// of een eerdere paneel-lookup) worden gevonden; de rest ontbreekt gewoon in het resultaat.
    /// Bestaat <c>his.matches</c> nog niet (verse database, nog nooit gesynchroniseerd — de tabel
    /// wordt dynamisch aangemaakt), dan is het antwoord leeg, geen fout.
    /// </summary>
    internal static async Task<Dictionary<string, SportlinkWedstrijdContext>> ZoekWedstrijdenBijPublicMatchIdsAsync(
        SqlConnection connection, IReadOnlyCollection<string> publicMatchIds, string clubCode)
    {
        var resultaat = new Dictionary<string, SportlinkWedstrijdContext>(StringComparer.Ordinal);
        if (publicMatchIds.Count == 0) return resultaat;

        using var cmd = new SqlCommand();
        cmd.Connection = connection;
        // SQL Server kent geen "= ANY(@array)": elk ID krijgt een eigen parameter, zodat de waarden
        // nooit in de querytekst belanden (geen string-concatenatie van clientinvoer).
        var idParams = new List<string>(publicMatchIds.Count);
        var volgnummer = 0;
        foreach (var id in publicMatchIds)
        {
            var naam = "@Id" + volgnummer++;
            idParams.Add(naam);
            cmd.Parameters.AddWithValue(naam, id);
        }

        cmd.CommandText = $@"
            SELECT c.[PublicMatchId], m.[wedstrijdcode], m.[wedstrijdnummer], m.[thuisteam], m.[uitteam],
                   CONVERT(VARCHAR(10), CAST(m.[kaledatum] AS DATE), 23), m.[aanvangstijd], m.[accommodatie]
            FROM [dbo].[SportlinkPublicMatchIdCache] c
            INNER JOIN [his].[matches] m
                ON m.[wedstrijdcode] = c.[Wedstrijdcode]
               AND {ClubScope.HisFilter("m")}
               AND m.[mta_deleted] IS NULL
            WHERE c.[ClubCode] = {ClubScope.ClubCodeParam}
              AND c.[PublicMatchId] IN ({string.Join(", ", idParams)})";
        ClubScope.AddHisParams(cmd, clubCode);

        try
        {
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var publicMatchId = reader.GetString(0);
                resultaat[publicMatchId] = new SportlinkWedstrijdContext(
                    Wedstrijdcode: reader.GetInt64(1),
                    Wedstrijdnummer: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    Thuisteam: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Uitteam: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Datum: reader.IsDBNull(5) ? null : reader.GetString(5),
                    Tijd: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Accommodatie: reader.IsDBNull(7) ? null : reader.GetString(7));
            }
        }
        catch (SqlException ex) when (ex.Number == SqlFoutOngeldigeObjectnaam)
        {
            // his.matches bestaat pas na de eerste sync — geen context, geen fout.
        }
        return resultaat;
    }

    internal static async Task<string?> LeesUitCacheAsync(SqlConnection connection, long wedstrijdcode, string clubCode)
    {
        using var cmd = new SqlCommand(@"
            SELECT [PublicMatchId] FROM [dbo].[SportlinkPublicMatchIdCache]
            WHERE [Wedstrijdcode] = @Wedstrijdcode AND [ClubCode] = @ClubCode", connection);
        cmd.Parameters.AddWithValue("@Wedstrijdcode", wedstrijdcode);
        cmd.Parameters.AddWithValue("@ClubCode", clubCode);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    /// <summary>Schrijft of vervangt de cache-rij. <c>MERGE ... WITH (HOLDLOCK)</c> is het
    /// SQL Server-equivalent van Postgres' <c>ON CONFLICT ... DO UPDATE</c>: zonder die hint kunnen
    /// twee gelijktijdige warmup-aanroepen beide de INSERT-tak kiezen en op de primaire sleutel
    /// botsen. <c>GETUTCDATE()</c>, nooit <c>GETDATE()</c> (UTC-regel, #246).</summary>
    internal static async Task SchrijfInCacheAsync(
        SqlConnection connection, long wedstrijdcode, string clubCode, string publicMatchId)
    {
        using var cmd = new SqlCommand(@"
            MERGE [dbo].[SportlinkPublicMatchIdCache] WITH (HOLDLOCK) AS doel
            USING (VALUES (@Wedstrijdcode, @ClubCode, @PublicMatchId))
                AS bron ([Wedstrijdcode], [ClubCode], [PublicMatchId])
              ON doel.[Wedstrijdcode] = bron.[Wedstrijdcode] AND doel.[ClubCode] = bron.[ClubCode]
            WHEN MATCHED THEN
                UPDATE SET [PublicMatchId] = bron.[PublicMatchId], [OpgehaaldOp] = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT ([Wedstrijdcode], [ClubCode], [PublicMatchId], [OpgehaaldOp])
                VALUES (bron.[Wedstrijdcode], bron.[ClubCode], bron.[PublicMatchId], GETUTCDATE());", connection);
        cmd.Parameters.AddWithValue("@Wedstrijdcode", wedstrijdcode);
        cmd.Parameters.AddWithValue("@ClubCode", clubCode);
        cmd.Parameters.AddWithValue("@PublicMatchId", publicMatchId);
        await cmd.ExecuteNonQueryAsync();
    }
}
