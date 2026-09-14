using Database.Postgres;
using Npgsql;
using Planner.Shared;

namespace FunctionApp.Postgres.Planner;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Planner/Repositories/AllstarsTestDataRepository.cs</c> (#888).
/// <c>GetAllstarsVeldenAsync</c> en <c>UpdateAllstarsMatchAsync</c> horen bij de auto-plan-/
/// testdata-schrijfpaden die buiten de eerste #888-ronde vielen en zijn nog niet vertaald.
/// <para>
/// <b><see cref="GetTeamleiderContactAsync"/> is sinds #1140 vertaald</b> (deelstuk 2 van #972) —
/// tot dan gaf <c>BerichtPipeline</c>'s <c>TeamContactOpvragen</c>-tak hier altijd
/// <c>coachGevonden = false</c> terug. De matching-sleutel is bewust anders dan het SQL Server-
/// origineel: dat vergelijkt met een eigen <c>REPLACE(...,' ','')REPLACE(...,'-','')</c>-sleutel
/// rechtstreeks in T-SQL; hier wordt in plaats daarvan
/// <see cref="TeamNaamNormalisatie.NormaliseerVoorVergelijking"/> gebruikt — de enige toegestane
/// teamnaam-normalisatielaag (CLAUDE.md, #692/#889) — toegepast in C# op elke kandidaatrij, in
/// plaats van een tweede ad-hoc regex/REPLACE-implementatie in SQL te bouwen. Zelfde precedent als
/// <c>PlannerMatchRepository.TeamSchrijfwijzenAsync</c>/<c>FindMatchByOpponentAsync</c> (#1139).
/// Functioneel gelijk gedrag: zowel de lokale notatie ("JO13-1") als de KNVB-notatie ("O13-1")
/// normaliseren naar dezelfde sleutel, dus geen aparte "knvbSleutel"-tweede parameter nodig zoals
/// op de SQL Server-tier.
/// </para>
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

    /// <summary>
    /// AVG: levert persoonsgegevens — uitsluitend voor interne notificaties. Selecteert daarom
    /// alleen <c>naam</c>/<c>emailadres</c>, nooit team, teamrol of telefoonnummer.
    ///
    /// <para>
    /// Postgres-vertaling van het gelijknamige SQL Server-origineel (#1140, deelstuk 2 van #972) —
    /// zie de klassekop voor de bewuste afwijking in de matching-sleutel
    /// (<see cref="TeamNaamNormalisatie.NormaliseerVoorVergelijking"/> in plaats van een tweede
    /// REPLACE-gebaseerde sleutel in SQL). De rolvoorkeur-volgorde (trainer &gt; coach &gt;
    /// teamleider &gt; technische &gt; overig) en de uitsluiting van de "Medische"-rol zijn
    /// woordelijk gelijk aan het origineel.
    /// </para>
    /// </summary>
    internal static async Task<TeamleiderContact?> GetTeamleiderContactAsync(
        string connectionString, string teamNaam, string? clubCode = null)
    {
        var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(teamNaam, PostgresClubScope.Primary);
        if (sleutel.Length == 0) return null;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($@"
            SELECT tb.team, tb.teamrol, tb.naam, tb.emailadres
            FROM avg.teambegeleiding tb
            WHERE tb.emailadres IS NOT NULL AND tb.emailadres <> ''
              AND tb.teamrol NOT ILIKE '%medische%'
              AND {PostgresClubScope.LegacyFilter("tb")}
        ", conn);
        PostgresClubScope.AddHisParams(cmd, clubCode);

        var kandidaten = new List<(string Teamrol, string Naam, string Emailadres)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var team = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (TeamNaamNormalisatie.NormaliseerVoorVergelijking(team) != sleutel) continue;

                kandidaten.Add((
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3)));
            }
        }
        if (kandidaten.Count == 0) return null;

        var beste = kandidaten
            .OrderBy(k => RolPrioriteit(k.Teamrol))
            .ThenBy(k => k.Naam, StringComparer.OrdinalIgnoreCase)
            .First();

        return new TeamleiderContact { Naam = beste.Naam, Emailadres = beste.Emailadres };
    }

    /// <summary>Zelfde rolvoorkeur-volgorde als de CASE-expressie van het SQL Server-origineel.</summary>
    private static int RolPrioriteit(string teamrol) => teamrol switch
    {
        _ when teamrol.Contains("Trainer", StringComparison.OrdinalIgnoreCase) => 1,
        _ when teamrol.Contains("Coach", StringComparison.OrdinalIgnoreCase) => 2,
        _ when teamrol.Contains("leider", StringComparison.OrdinalIgnoreCase) => 3,
        _ when teamrol.Contains("Technische", StringComparison.OrdinalIgnoreCase) => 4,
        _ => 5
    };
}

internal sealed record WedstrijdRaw(
    long? WedstrijdCode, string Wedstrijd, string TeamNaam, string? Uitteam,
    string? AanvangsTijd, string? Veld, string? Competitiesoort, string? LeeftijdsCategorie);

/// <summary>
/// AVG: bevat persoonsgegevens — uitsluitend voor interne notificaties. Postgres-tier-tegenhanger
/// van <c>SportlinkFunction.Planner.TeamleiderContact</c> (#1140); geen gedeeld model, want dat is
/// per <c>docs/ARCHITECTUUR-DATABASE-TIERS.md</c> een bewuste keuze — elke tier krijgt een eigen,
/// volledig gescheiden implementatieboom.
/// </summary>
internal sealed class TeamleiderContact
{
    public string Naam { get; set; } = string.Empty;
    public string Emailadres { get; set; } = string.Empty;
}
