using Database.Postgres;
using Npgsql;
using Planner.Shared;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Planner/Repositories/AllstarsTestDataRepository.cs</c> (#888). Alleen
/// <see cref="GetAllMatchesForDatumAsync"/> is vertaald — nodig voor het
/// <c>GET /api/planner/veldbezetting</c>-endpoint. <c>GetAllstarsVeldenAsync</c> en
/// <c>UpdateAllstarsMatchAsync</c> horen bij de auto-plan-/testdata-schrijfpaden die buiten deze
/// eerste #888-ronde vallen en zijn nog niet vertaald. <c>GetTeamleiderContactAsync</c> heeft
/// sinds #889 wél een consument (<c>BerichtPipeline</c>'s <c>TeamContactOpvragen</c>-tak, die hier
/// altijd <c>coachGevonden = false</c> teruggeeft zolang dit ontbreekt) — expliciet vastgelegd als
/// vervolgwerk in #972, niet stilzwijgend overgeslagen.
/// <para>
/// <b>OUTER APPLY → LATERAL JOIN</b> (#888's genoemde valkuil): de niet-ALLSTARS-tak gebruikte
/// <c>OUTER APPLY (SELECT TOP 1 …) t</c> om per wedstrijd het team op te zoeken. Postgres-
/// equivalent: <c>LEFT JOIN LATERAL (SELECT … LIMIT 1) t ON TRUE</c> — empirisch geverifieerd
/// tegen een Postgres-instantie (zie PR-beschrijving).
/// </para>
/// </summary>
internal static class AllstarsTestDataRepository
{
    internal static async Task<List<WedstrijdRaw>> GetAllMatchesForDatumAsync(
        string connectionString, DateOnly datum, string clubCode)
    {
        bool isAllstars = clubCode.Equals("ALLSTARS", StringComparison.OrdinalIgnoreCase);
        var results = new List<WedstrijdRaw>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        string sql = isAllstars
            ? @"SELECT m.wedstrijdcode,
                       COALESCE(NULLIF(m.wedstrijd, ''),
                                COALESCE(m.teamnaam, '') || ' - ' ||
                                COALESCE(CASE WHEN m.teamnaam = m.thuisteam
                                              THEN m.uitteam ELSE m.thuisteam END, '')) AS wedstrijd,
                       m.teamnaam,
                       CASE WHEN m.teamnaam = m.thuisteam
                            THEN m.uitteam ELSE m.thuisteam END AS uitteam,
                       m.aanvangstijd, m.veld, m.competitiesoort,
                       NULL AS leeftijdscategorie
                FROM his.matches m
                WHERE m.kaledatum::date = @date
                  AND m.clubcode = 'ALLSTARS'
                  AND (m.status IS NULL OR m.status <> 'Afgelast')
                ORDER BY m.teamnaam"
            : $@"SELECT m.wedstrijdcode, m.wedstrijd, m.teamnaam, m.uitteam,
                       m.aanvangstijd, m.veld, m.competitiesoort,
                       {PostgresLeeftijdNormalisatie.SqlExpr("COALESCE(t.leeftijdscategorie, '')")} AS leeftijdscategorie
                FROM his.matches m
                LEFT JOIN LATERAL (
                    SELECT leeftijdscategorie
                    FROM his.teams
                    WHERE teamnaam = m.teamnaam AND clubcode = m.clubcode
                    LIMIT 1
                ) t ON TRUE
                WHERE m.kaledatum::date = @date
                  AND m.clubcode = @clubCode
                  AND m.status <> 'Afgelast'
                  AND m.accommodatie LIKE '%' || (SELECT accommodatie FROM public.appsettings WHERE clubcode = @clubCode LIMIT 1) || '%'
                ORDER BY m.teamvolgorde, m.teamnaam";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("date", datum.ToDateTime(TimeOnly.MinValue));
        if (!isAllstars) cmd.Parameters.AddWithValue("clubCode", clubCode);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(new WedstrijdRaw(
                WedstrijdCode: reader.IsDBNull(0) ? null : reader.GetInt64(0),
                Wedstrijd: reader.IsDBNull(1) ? "" : reader.GetString(1),
                TeamNaam: reader.IsDBNull(2) ? "" : reader.GetString(2),
                Uitteam: reader.IsDBNull(3) ? null : reader.GetString(3),
                AanvangsTijd: reader.IsDBNull(4) ? null : reader.GetString(4)?.Trim(),
                Veld: reader.IsDBNull(5) ? null : reader.GetString(5)?.Trim(),
                Competitiesoort: reader.IsDBNull(6) ? null : reader.GetString(6),
                LeeftijdsCategorie: reader.IsDBNull(7) ? null :
                    (string.IsNullOrWhiteSpace(reader.GetString(7)) ? null : reader.GetString(7))));
        return results;
    }

    /// <summary>
    /// De demovelden van de testmodus: veldnummers &gt;= 100 (issue 888 vervolg, §42).
    /// Postgres-vertaling van het gelijknamige SQL Server-origineel.
    /// <para>
    /// De grens op 100 is dezelfde afspraak als in <c>006_allstars_demodata.sql</c>, dat de
    /// democlub bewust de nummers 101-103 geeft om een PK-botsing met de primaire club te vermijden
    /// (<c>public.velden.veldnummer</c> is een kale PK zonder ClubCode-scope). Deze query filtert
    /// dus op precies dezelfde conventie — géén tweede, eigen afspraak.
    /// </para>
    /// </summary>
    internal static async Task<List<VeldInfo>> GetAllstarsVeldenAsync(string connectionString)
    {
        var results = new List<VeldInfo>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT veldnummer, veldnaam, COALESCE(veldtype, 'kunstgras'), heeftkunstlicht
            FROM public.velden
            WHERE actief = true AND veldnummer >= 100
            ORDER BY veldnummer
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
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

    /// <summary>
    /// Schrijft een AutoPlan-resultaat terug op één demowedstrijd (issue 888 vervolg, §42).
    /// Uitsluitend rijen met <c>clubcode = 'ALLSTARS'</c> — dit is een testmodus-schrijfpad en mag
    /// nooit echte clubdata raken. <c>GETUTCDATE()</c> → <c>NOW()</c>: de kolom is
    /// <c>TIMESTAMPTZ</c>, dus Postgres bewaart het tijdstip sowieso tijdzone-bewust.
    /// </summary>
    internal static async Task<int> UpdateAllstarsMatchAsync(
        string connectionString, long wedstrijdCode, string nieuweVeld, string nieuweTijd)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            UPDATE his.matches
            SET aanvangstijd = @tijd, veld = @veld, mta_modified = NOW()
            WHERE wedstrijdcode = @code AND clubcode = 'ALLSTARS'
            """, conn);
        cmd.Parameters.AddWithValue("tijd", nieuweTijd);
        cmd.Parameters.AddWithValue("veld", nieuweVeld);
        cmd.Parameters.AddWithValue("code", wedstrijdCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}

internal sealed record WedstrijdRaw(
    long? WedstrijdCode, string Wedstrijd, string TeamNaam, string? Uitteam,
    string? AanvangsTijd, string? Veld, string? Competitiesoort, string? LeeftijdsCategorie);
