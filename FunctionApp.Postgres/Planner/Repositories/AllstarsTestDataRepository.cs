using Database.Postgres;
using Npgsql;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Planner/Repositories/AllstarsTestDataRepository.cs</c> (#888). Alleen
/// <see cref="GetAllMatchesForDatumAsync"/> is vertaald — nodig voor het
/// <c>GET /api/planner/veldbezetting</c>-endpoint. <c>GetAllstarsVeldenAsync</c>,
/// <c>UpdateAllstarsMatchAsync</c> en <c>GetTeamleiderContactAsync</c> zijn nog niet vertaald;
/// die horen bij de auto-plan-/testdata-schrijfpaden die buiten deze eerste #888-ronde vallen.
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
}

internal sealed record WedstrijdRaw(
    long? WedstrijdCode, string Wedstrijd, string TeamNaam, string? Uitteam,
    string? AanvangsTijd, string? Veld, string? Competitiesoort, string? LeeftijdsCategorie);
