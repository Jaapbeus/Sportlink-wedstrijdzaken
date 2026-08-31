using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared;

namespace FunctionApp.Postgres.Planner.Repositories;

/// <summary>
/// Postgres-tier-tegenhanger van (een deel van) <c>FunctionApp/Planner/Repositories/PlannerMatchRepository.cs</c>
/// (#888). Vertaald zijn <see cref="MarkeerVervallenGeplandeWedstrijdenAsync"/> (#890), de twee
/// leesmethoden achter <c>GET /api/planner/team-schedule</c> (<see cref="TeamExistsAsync"/>,
/// <see cref="GetFutureMatchesForTeamAsync"/>), en — sinds #888 vervolg — de drie kleinste van de
/// vier resterende gaten die de klasse-doc-comment van <c>PlannerFunction.cs</c> ooit noemde:
/// <see cref="FindMatchAsync"/>, <see cref="FindMatchByCodeAsync"/>,
/// <see cref="SavePlannedMatchAsync"/>, <see cref="SaveHerplanVerzoekAsync"/> — genoeg om
/// <c>ZoekWedstrijd</c>, <c>BevestigWedstrijd</c> en <c>HerplanBevestig</c> echt te wireren — en,
/// sinds #888 vervolg/§41, <see cref="GetTeamMatchesOnDateAsync"/> (nodig voor
/// <c>AvailabilityService</c>'s team-conflictcontrole).
/// <para>
/// Nog niet vertaald: <c>GetGeplandeWedstrijdenOnlyAsync</c> en <c>FindMatchByOpponentAsync</c> —
/// geen consument op deze tier (het eerste hoort bij een los "wat staat er gepland"-endpoint dat
/// niet bestaat, het tweede bij de e-mail-AI-antwoordflow, buiten deze epic se scope). Zie
/// docs/ARCHITECTUUR-DATABASE-TIERS.md §16/§40/§41.
/// </para>
/// </summary>
internal static class PlannerMatchRepository
{
    /// <summary>
    /// Alle bekende schrijfwijzen van één team: de canonieke naam plus elke gevalideerde alias.
    /// Postgres-vertaling van het gelijknamige SQL Server-origineel (#700).
    /// <para>
    /// <b>Twee vertaalpunten die er toe doen.</b> Het origineel is een T-SQL-batch met een
    /// <c>DECLARE @teamId</c> plus een vroege <c>RETURN</c>; dat bestaat in Postgres niet buiten een
    /// functie of DO-blok. Het is hier één query met een CTE die hetzelfde <c>COALESCE</c> van twee
    /// scalaire subquery's doet — vindt die niets, dan levert de CTE <c>NULL</c> en matcht geen
    /// enkele rij, wat exact het gedrag van de <c>RETURN</c> is.
    /// </para>
    /// <para>
    /// En #820: elke sleutelvergelijking staat in <c>UPPER(...)</c>. Op de SQL Server-tier doet de
    /// <c>Latin1_General_CI_AS</c>-collatie dat impliciet; Postgres' default-collatie is
    /// case-sensitive, dus zonder deze wrap zou een historische rij met afwijkende casing
    /// stilzwijgend nul resultaten geven. Zelfde patroon als <c>TeamCandidateRepository</c>.
    /// </para>
    /// </summary>
    private static async Task<List<string>> TeamSchrijfwijzenAsync(
        NpgsqlConnection conn, string clubCode, string? teamNaam)
    {
        var resultaten = new List<string>();
        var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(teamNaam, clubCode);
        if (sleutel.Length == 0) return resultaten;

        await using var cmd = new NpgsqlCommand(@"
            WITH gevonden AS (
                SELECT COALESCE(
                    (SELECT t.teamid
                       FROM public.teams t
                      WHERE t.clubcode = @clubcode
                        AND UPPER(t.teamnaamgenormaliseerd) = UPPER(@sleutel)
                        AND t.isactief = TRUE
                      LIMIT 1),
                    (SELECT a.teamid
                       FROM public.teamaliassen a
                       INNER JOIN public.teams t2 ON t2.teamid = a.teamid AND t2.isactief = TRUE
                      WHERE a.clubcode = @clubcode
                        AND a.status = 'validated'
                        AND (UPPER(a.ruwetekst) = UPPER(@ruwetekst)
                          OR UPPER(a.ruwetekstgenormaliseerd) = UPPER(@sleutel))
                      LIMIT 1)
                ) AS teamid
            )
            SELECT t.teamnaam
              FROM public.teams t INNER JOIN gevonden g ON g.teamid = t.teamid
            UNION
            SELECT a.ruwetekst
              FROM public.teamaliassen a INNER JOIN gevonden g ON g.teamid = a.teamid
             WHERE a.clubcode = @clubcode AND a.status = 'validated'
        ", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("sleutel", sleutel);
        cmd.Parameters.AddWithValue("ruwetekst", (teamNaam ?? "").Trim());

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (!reader.IsDBNull(0)) resultaten.Add(reader.GetString(0));
        return resultaten;
    }

    /// <summary>
    /// De vergelijkingssleutels voor een schrijfwijzen-filter: getrimd én ge-upper'd.
    /// <para>
    /// <b>Waarom trimmen — een tweede, los engineverschil naast de collatie (#820).</b> SQL Server
    /// negeert bij <c>=</c> en <c>IN</c> op <c>varchar</c> de spaties aan het eind (ANSI-padding),
    /// Postgres niet: daar is <c>'JO13-1 '</c> ongelijk aan <c>'JO13-1'</c>. Zonder de trim zou een
    /// wedstrijdrij met een afsluitende spatie in <c>teamnaam</c> — afkomstig uit de externe bron,
    /// dus buiten onze controle — hier stilzwijgend uit het teamrooster verdwijnen, terwijl dezelfde
    /// rij op de SQL Server-tier gewoon meetelt.
    /// </para>
    /// </summary>
    private static string[] Vergelijkingssleutels(IReadOnlyList<string> schrijfwijzen) =>
        schrijfwijzen.Select(s => s.Trim().ToUpperInvariant()).Distinct().ToArray();

    /// <inheritdoc cref="TeamSchrijfwijzenAsync"/>
    /// <remarks>
    /// Vraagt de canonieke teamlijst, niet <c>his.teams</c> rechtstreeks: die laatste bevat elk team
    /// in meerdere schrijfwijzen, waardoor een vergelijking op de ruwe naam afhankelijk werd van
    /// welke notatie de aanroeper toevallig gebruikte (#700).
    /// </remarks>
    internal static async Task<bool> TeamExistsAsync(
        string connectionString, string team, string clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return (await TeamSchrijfwijzenAsync(conn, clubCode, team)).Count > 0;
    }

    /// <summary>
    /// Alle wedstrijden van één team tussen twee datums: de gesynchroniseerde wedstrijden uit
    /// <c>his.matches</c> plus de zelf ingeplande oefenwedstrijden uit
    /// <c>planner.geplandewedstrijden</c>.
    /// </summary>
    internal static async Task<List<TeamScheduleWedstrijd>> GetFutureMatchesForTeamAsync(
        string connectionString, string team, DateOnly van, DateOnly tot, string clubCode)
    {
        var results = new List<TeamScheduleWedstrijd>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var schrijfwijzen = await TeamSchrijfwijzenAsync(conn, clubCode, team);
        if (schrijfwijzen.Count == 0) return results;
        var sleutels = Vergelijkingssleutels(schrijfwijzen);

        // De schrijfwijzen gaan als één array-parameter mee (= ANY(...)) in plaats van als een
        // dynamisch opgebouwde IN-lijst met genummerde parameters. Zelfde semantiek, maar de
        // querytekst is dan niet meer afhankelijk van het aantal aliassen.
        await using (var cmd = new NpgsqlCommand(@"
            SELECT m.kaledatum::date, m.aanvangstijd,
                   m.thuisteam, m.uitteam, m.competitiesoort, m.veld,
                   m.wedstrijdcode
            FROM his.matches m
            WHERE m.kaledatum::date BETWEEN @van AND @tot
              AND UPPER(m.status) <> 'AFGELAST'
              AND UPPER(TRIM(m.teamnaam)) = ANY(@sleutels)
              AND " + PostgresClubScope.HisFilter("m") + @"
            ORDER BY m.kaledatum::date, m.aanvangstijd
        ", conn))
        {
            cmd.Parameters.AddWithValue("van", van.ToDateTime(TimeOnly.MinValue).Date);
            cmd.Parameters.AddWithValue("tot", tot.ToDateTime(TimeOnly.MinValue).Date);
            cmd.Parameters.AddWithValue("sleutels", sleutels);
            PostgresClubScope.AddHisParams(cmd, clubCode);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var datum     = DateOnly.FromDateTime(reader.GetDateTime(0));
                var aanvang   = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                var thuisTeam = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
                var uitTeam   = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim();
                bool isThuis  = sleutels.Contains(thuisTeam.ToUpperInvariant());
                results.Add(new TeamScheduleWedstrijd
                {
                    Datum = datum.ToString("yyyy-MM-dd"),
                    AanvangsTijd = aanvang,
                    ThuisUit = isThuis ? "thuis" : "uit",
                    Tegenstander = isThuis ? uitTeam : thuisTeam,
                    Type = DetermineMatchType(reader.IsDBNull(4) ? "" : reader.GetString(4)),
                    Veld = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                    Wedstrijdcode = reader.IsDBNull(6) ? null : reader.GetInt64(6)
                });
            }
        }

        // planner.geplandewedstrijden.status wordt door de applicatie zelf gezet (kolomdefault
        // 'Te bevestigen'), dus daar is een kale vergelijking correct. m.status hierboven komt uit
        // de externe bron en staat daarom wél in UPPER(...).
        await using (var cmd2 = new NpgsqlCommand(@"
            SELECT gw.datum, to_char(gw.aanvangstijd, 'HH24:MI:SS'),
                   gw.tegenstander, v.veldnaam
            FROM planner.geplandewedstrijden gw
            LEFT JOIN public.velden v ON v.veldnummer = gw.veldnummer AND v.clubcode = @clubcode
            WHERE gw.datum BETWEEN @van AND @tot
              AND gw.status <> 'Geannuleerd'
              AND UPPER(TRIM(gw.teamnaam)) = ANY(@sleutels)
              AND gw.clubcode = @clubcode
            ORDER BY gw.datum, gw.aanvangstijd
        ", conn))
        {
            cmd2.Parameters.AddWithValue("van", van.ToDateTime(TimeOnly.MinValue).Date);
            cmd2.Parameters.AddWithValue("tot", tot.ToDateTime(TimeOnly.MinValue).Date);
            cmd2.Parameters.AddWithValue("sleutels", sleutels);
            cmd2.Parameters.AddWithValue("clubcode", PostgresClubScope.Resolve(clubCode));

            await using var reader2 = await cmd2.ExecuteReaderAsync();
            while (await reader2.ReadAsync())
                results.Add(new TeamScheduleWedstrijd
                {
                    Datum = DateOnly.FromDateTime(reader2.GetDateTime(0)).ToString("yyyy-MM-dd"),
                    AanvangsTijd = reader2.GetString(1),
                    ThuisUit = "thuis",
                    Tegenstander = reader2.IsDBNull(2) ? "" : reader2.GetString(2),
                    Type = "oefenwedstrijd",
                    Veld = reader2.IsDBNull(3) ? null : reader2.GetString(3),
                    Wedstrijdcode = null
                });
        }

        // Ordinale sortering op de tekstuele datum/tijd — identiek aan het origineel, en correct
        // omdat beide velden een vaste breedte hebben (yyyy-MM-dd, HH:mm).
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

    /// <summary>
    /// Alle wedstrijden van één team op één datum — gesynchroniseerde wedstrijden uit
    /// <c>his.matches</c> plus zelf ingeplande wedstrijden uit <c>planner.geplandewedstrijden</c>.
    /// Postgres-vertaling van het SQL Server-origineel (issue 888 vervolg, §41) — ontsluit
    /// <c>AvailabilityService.CheckAvailabilityAsync</c>'s team-conflictcontrole (mag een team niet
    /// twee wedstrijden op dezelfde dag krijgen).
    /// <para>
    /// <b>Veldresolutie in C#, net als <see cref="FindMatchAsync"/>'s buurman
    /// <see cref="Repositories.PlannerAvailabilityRepository"/>.</b> Het SQL Server-origineel
    /// gebruikt <c>VeldResolutie.SqlOuterApply</c> om <c>m.[veld]</c> naar een veldnummer op te
    /// lossen; hier gebeurt dat met <see cref="PlannerShared.VindVeldNummer"/> ná het uitlezen — de
    /// Postgres-tier heeft bewust geen vierde SQL-kopie van die matching (#819).
    /// </para>
    /// </summary>
    internal static async Task<List<BestaandeWedstrijd>> GetTeamMatchesOnDateAsync(
        string connectionString, string teamNaam, DateOnly date, string? clubCode)
    {
        var results = new List<BestaandeWedstrijd>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var cc = PostgresClubScope.Resolve(clubCode);
        var schrijfwijzen = await TeamSchrijfwijzenAsync(conn, cc, teamNaam);
        if (schrijfwijzen.Count == 0) return results;
        var sleutels = Vergelijkingssleutels(schrijfwijzen);

        var velden = await PlannerSettingsRepository.GetVeldenAsync(connectionString, cc);

        await using (var cmd = new NpgsqlCommand($@"
            SELECT m.kaledatum::date, m.aanvangstijd, COALESCE(s.wedstrijdtotaal, 0),
                   m.veld, m.wedstrijd
            FROM his.matches m
            LEFT JOIN his.teams t ON t.teamnaam = m.teamnaam
                 AND {PostgresClubScope.HisFilter("t")}
            LEFT JOIN public.speeltijden s ON s.leeftijd = {Database.Postgres.PostgresLeeftijdNormalisatie.SqlExpr("t.leeftijdscategorie")}
                 AND s.clubcode = {PostgresClubScope.ClubCodeParam}
            WHERE m.kaledatum::date = @date
              AND UPPER(m.status) <> 'AFGELAST'
              AND UPPER(TRIM(m.teamnaam)) = ANY(@sleutels)
              AND {PostgresClubScope.HisFilter("m")}
        ", conn))
        {
            cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue).Date);
            cmd.Parameters.AddWithValue("sleutels", sleutels);
            PostgresClubScope.AddHisParams(cmd, clubCode);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var aanvangstijd = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                var duur = reader.GetInt32(2);
                var naam = reader.IsDBNull(4) ? "onbekend" : reader.GetString(4);
                if (duur <= 0)
                    throw new InvalidOperationException(
                        $"Speelduur niet geconfigureerd voor wedstrijd '{naam}'. Voeg de leeftijdscategorie toe aan public.speeltijden via /instellingen/speeltijden.");
                if (!TimeOnly.TryParse(aanvangstijd, out var start)) continue;
                var veldRuw = reader.IsDBNull(3) ? null : reader.GetString(3);
                results.Add(new BestaandeWedstrijd
                {
                    Datum = DateOnly.FromDateTime(reader.GetDateTime(0)),
                    AanvangsTijd = start,
                    EindTijd = start.AddMinutes(duur),
                    VeldNummer = PlannerShared.VindVeldNummer(veldRuw, velden),
                    Wedstrijd = naam,
                    Bron = "Competitie"
                });
            }
        }

        await using (var cmd2 = new NpgsqlCommand(@"
            SELECT gw.datum, gw.aanvangstijd, gw.wedstrijdduurminuten, gw.veldnummer,
                   COALESCE(gw.teamnaam, '') || ' - ' || COALESCE(gw.tegenstander, '')
            FROM planner.geplandewedstrijden gw
            WHERE gw.datum = @date
              AND gw.status <> 'Geannuleerd'
              AND UPPER(TRIM(gw.teamnaam)) = ANY(@sleutels)
              AND gw.clubcode = @clubcode
        ", conn))
        {
            cmd2.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue).Date);
            cmd2.Parameters.AddWithValue("sleutels", sleutels);
            cmd2.Parameters.AddWithValue("clubcode", cc);

            await using var reader2 = await cmd2.ExecuteReaderAsync();
            while (await reader2.ReadAsync())
            {
                var aanvang = TimeOnly.FromTimeSpan(reader2.GetTimeSpan(1));
                var duur = reader2.GetInt32(2);
                results.Add(new BestaandeWedstrijd
                {
                    Datum = DateOnly.FromDateTime(reader2.GetDateTime(0)),
                    AanvangsTijd = aanvang,
                    EindTijd = aanvang.AddMinutes(duur),
                    VeldNummer = reader2.GetInt32(3),
                    Wedstrijd = reader2.GetString(4),
                    Bron = "Planner"
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Eén wedstrijd van een team op een datum — Postgres-vertaling van het SQL Server-origineel
    /// (#888 vervolg, ontsluit <c>POST /api/planner/zoek-wedstrijd</c>).
    /// <para>
    /// <b><c>VeldNaam</c> is hier bewust de RUWE Sportlink-veldstring</b> (<c>m.veld</c>), geen
    /// geresolveerd veldnummer — het origineel doet hier géén <c>VeldResolutie</c>-lookup, dit
    /// endpoint toont alleen zoekresultaten aan een beheerder. Vergelijk
    /// <see cref="Repositories.PlannerAvailabilityRepository"/>, dat wél resolveert omdat de
    /// FieldScheduler-engine een numeriek veldnummer nodig heeft.
    /// </para>
    /// </summary>
    internal static async Task<ZoekWedstrijdResponse?> FindMatchAsync(
        string connectionString, string teamNaam, DateOnly date, string? clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var cc = PostgresClubScope.Resolve(clubCode);
        var accommodatie = await PostgresClubScope.RequireAccommodatieAsync(conn, cc);

        var schrijfwijzen = await TeamSchrijfwijzenAsync(conn, cc, teamNaam);
        if (schrijfwijzen.Count == 0) return null;
        var sleutels = Vergelijkingssleutels(schrijfwijzen);

        await using var cmd = new NpgsqlCommand($@"
            SELECT m.wedstrijdcode, m.wedstrijd,
                   m.kaledatum::date, m.aanvangstijd,
                   COALESCE(s.wedstrijdtotaal, 0), m.veld,
                   t.leeftijdscategorie, COALESCE(s.veldafmeting, 1.00)
            FROM his.matches m
            LEFT JOIN his.teams t ON t.teamnaam = m.teamnaam
                 AND t.leeftijdscategorie IS NOT NULL AND t.leeftijdscategorie <> ''
                 AND {PostgresClubScope.HisFilter("t")}
            LEFT JOIN public.speeltijden s ON s.leeftijd = {Database.Postgres.PostgresLeeftijdNormalisatie.SqlExpr("t.leeftijdscategorie")}
                 AND s.clubcode = {PostgresClubScope.ClubCodeParam}
            WHERE m.kaledatum::date = @date
              AND m.accommodatie ILIKE @accommodatiepattern
              AND UPPER(m.status) <> 'AFGELAST'
              AND UPPER(TRIM(m.teamnaam)) = ANY(@sleutels)
              AND {PostgresClubScope.HisFilter("m")}
            ORDER BY m.aanvangstijd
            LIMIT 1
        ", conn);
        cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue).Date);
        cmd.Parameters.AddWithValue("accommodatiepattern", $"%{accommodatie}%");
        cmd.Parameters.AddWithValue("sleutels", sleutels);
        PostgresClubScope.AddHisParams(cmd, clubCode);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return LeesZoekWedstrijdResponse(reader, vasteDatum: date);
    }

    /// <summary>
    /// Eén wedstrijd op Sportlink-wedstrijdcode — Postgres-vertaling van het SQL Server-origineel
    /// (#888 vervolg, ontsluit <c>POST /api/planner/herplan-bevestig</c>). Bewust geen
    /// <c>status &lt;&gt; 'Afgelast'</c>-filter: dit pad zoekt een bekende wedstrijd op code voor een
    /// herplanverzoek, ook als de status inmiddels is gewijzigd — zelfde als het origineel.
    /// </summary>
    internal static async Task<ZoekWedstrijdResponse?> FindMatchByCodeAsync(
        string connectionString, long wedstrijdcode, string? clubCode)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var cc = PostgresClubScope.Resolve(clubCode);
        var accommodatie = await PostgresClubScope.RequireAccommodatieAsync(conn, cc);

        await using var cmd = new NpgsqlCommand($@"
            SELECT m.wedstrijdcode, m.wedstrijd,
                   m.kaledatum::date, m.aanvangstijd,
                   COALESCE(s.wedstrijdtotaal, 0), m.veld,
                   t.leeftijdscategorie, COALESCE(s.veldafmeting, 1.00)
            FROM his.matches m
            LEFT JOIN his.teams t ON t.teamnaam = m.teamnaam
                 AND t.leeftijdscategorie IS NOT NULL AND t.leeftijdscategorie <> ''
                 AND {PostgresClubScope.HisFilter("t")}
            LEFT JOIN public.speeltijden s ON s.leeftijd = {Database.Postgres.PostgresLeeftijdNormalisatie.SqlExpr("t.leeftijdscategorie")}
                 AND s.clubcode = {PostgresClubScope.ClubCodeParam}
            WHERE m.wedstrijdcode = @code
              AND m.accommodatie ILIKE @accommodatiepattern
              AND {PostgresClubScope.HisFilter("m")}
            LIMIT 1
        ", conn);
        cmd.Parameters.AddWithValue("code", wedstrijdcode);
        cmd.Parameters.AddWithValue("accommodatiepattern", $"%{accommodatie}%");
        PostgresClubScope.AddHisParams(cmd, clubCode);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return LeesZoekWedstrijdResponse(reader, vasteDatum: null);
    }

    /// <summary>
    /// Gedeelde rij-uitlezing voor <see cref="FindMatchAsync"/> en <see cref="FindMatchByCodeAsync"/>
    /// — beide queries selecteren exact dezelfde acht kolommen in dezelfde volgorde.
    /// <paramref name="vasteDatum"/> is de bekende zoekdatum (FindMatchAsync kreeg hem als
    /// parameter); bij <c>null</c> (FindMatchByCodeAsync) komt de datum uit de rij zelf.
    /// </summary>
    private static ZoekWedstrijdResponse LeesZoekWedstrijdResponse(NpgsqlDataReader reader, DateOnly? vasteDatum)
    {
        var aanvangstijd = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim();
        var duur = reader.GetInt32(4);
        var naam = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
        if (duur <= 0)
            throw new InvalidOperationException(
                $"Speelduur niet geconfigureerd voor wedstrijd '{naam}'. Voeg de leeftijdscategorie toe aan public.speeltijden via /instellingen/speeltijden.");
        TimeOnly.TryParse(aanvangstijd, out var start);
        var datum = vasteDatum ?? DateOnly.FromDateTime(reader.GetDateTime(2));
        return new ZoekWedstrijdResponse
        {
            Wedstrijdcode = reader.GetInt64(0),
            Wedstrijd = naam,
            Datum = datum.ToString("yyyy-MM-dd"),
            AanvangsTijd = aanvangstijd,
            EindTijd = start.AddMinutes(duur).ToString("HH:mm"),
            DuurMinuten = duur,
            VeldNaam = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
            LeeftijdsCategorie = reader.IsDBNull(6) ? null : reader.GetString(6).Trim(),
            VeldDeelGebruik = reader.GetDecimal(7)
        };
    }

    /// <summary>
    /// Slaat een handmatig ingeplande wedstrijd op — Postgres-vertaling van het SQL Server-origineel
    /// (#888 vervolg, ontsluit <c>POST /api/planner/bevestig</c>). <c>RETURNING id</c> i.p.v.
    /// <c>OUTPUT INSERTED.Id</c>, verder één-op-één dezelfde kolommen en dezelfde harde
    /// <c>'Te bevestigen'</c>-startstatus.
    /// </summary>
    internal static async Task<int> SavePlannedMatchAsync(
        string connectionString,
        DateOnly datum, TimeOnly aanvangsTijd, TimeOnly eindTijd, int veldNummer,
        decimal veldDeelGebruik, string? leeftijdsCategorie, string? teamNaam,
        string? tegenstander, int wedstrijdDuurMinuten, string? aangevraagdDoor,
        string? clubCode)
    {
        var cc = PostgresClubScope.Resolve(clubCode);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO planner.geplandewedstrijden
                (datum, aanvangstijd, eindtijd, veldnummer, velddeelgebruik,
                 leeftijdscategorie, teamnaam, tegenstander, wedstrijdduurminuten,
                 status, aangevraagddoor, clubcode)
            VALUES (@datum, @aanvang, @eind, @veld, @deel, @cat, @team, @tegen, @duur, 'Te bevestigen', @door, @cc)
            RETURNING id
        ", conn);
        cmd.Parameters.AddWithValue("datum", datum.ToDateTime(TimeOnly.MinValue).Date);
        cmd.Parameters.AddWithValue("aanvang", aanvangsTijd.ToTimeSpan());
        cmd.Parameters.AddWithValue("eind", eindTijd.ToTimeSpan());
        cmd.Parameters.AddWithValue("veld", veldNummer);
        cmd.Parameters.AddWithValue("deel", veldDeelGebruik);
        cmd.Parameters.AddWithValue("cat", (object?)leeftijdsCategorie ?? DBNull.Value);
        cmd.Parameters.AddWithValue("team", (object?)teamNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tegen", (object?)tegenstander ?? DBNull.Value);
        cmd.Parameters.AddWithValue("duur", wedstrijdDuurMinuten);
        cmd.Parameters.AddWithValue("door", (object?)aangevraagdDoor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cc", cc);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Legt een herplanverzoek vast — Postgres-vertaling van het SQL Server-origineel (#888
    /// vervolg, ontsluit <c>POST /api/planner/herplan-bevestig</c>).
    /// <para>
    /// <b>Bevat, in tegenstelling tot het huidige SQL Server-origineel, wél <c>ClubCode</c>.</b>
    /// Tijdens deze vertaling bleek de SQL Server-kant <c>ClubCode</c> volledig te missen in de
    /// INSERT terwijl de kolom <c>NOT NULL</c> is zonder <c>DEFAULT</c> — elke aanroep zou daar een
    /// SQL-fout gooien. Apart gefixt in <c>FunctionApp/Planner/Repositories/PlannerMatchRepository.cs</c>
    /// (zelfde commit); de Postgres-versie is vanaf het begin correct.
    /// </para>
    /// </summary>
    internal static async Task<int> SaveHerplanVerzoekAsync(
        string connectionString,
        long wedstrijdcode, string huidigeWedstrijd, DateOnly huidigeDatum,
        TimeOnly huidigeAanvangsTijd, string? huidigeVeldNaam,
        TimeOnly gewensteAanvangsTijd, int? gewenstVeldNummer,
        string? aangevraagdDoor, string? opmerking, string? clubCode)
    {
        var cc = PostgresClubScope.Resolve(clubCode);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO planner.herplanverzoeken
                (wedstrijdcode, huidigewedstrijd, huidigedatum, huidigeaanvangstijd,
                 huidigeveldnaam, gewensteaanvangstijd, gewenstveldnummer,
                 status, aangevraagddoor, opmerking, clubcode)
            VALUES (@code, @wedstrijd, @datum, @aanvang, @veld, @gewensteTijd, @gewenstVeld, 'Aangevraagd', @door, @opmerking, @cc)
            RETURNING id
        ", conn);
        cmd.Parameters.AddWithValue("code", wedstrijdcode);
        cmd.Parameters.AddWithValue("wedstrijd", huidigeWedstrijd);
        cmd.Parameters.AddWithValue("datum", huidigeDatum.ToDateTime(TimeOnly.MinValue).Date);
        cmd.Parameters.AddWithValue("aanvang", huidigeAanvangsTijd.ToTimeSpan());
        cmd.Parameters.AddWithValue("veld", (object?)huidigeVeldNaam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("gewensteTijd", gewensteAanvangsTijd.ToTimeSpan());
        cmd.Parameters.AddWithValue("gewenstVeld", (object?)gewenstVeldNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("door", (object?)aangevraagdDoor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("opmerking", (object?)opmerking ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cc", cc);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Postgres-vertaling van het SQL Server-origineel (<c>PlannerMatchRepository.MarkeerVervallenGeplandeWedstrijdenAsync</c>).
    /// Zelfde teamalias-gebaseerde matching (#700) i.p.v. een rechtstreekse teamnaamvergelijking:
    /// de teamnaam die in <c>planner.geplandewedstrijden</c> staat en de teamnaam die in
    /// <c>his.matches</c> binnenkomt gebruiken verschillende schrijfwijzen, dus beide kanten worden
    /// via gevalideerde aliassen naar hetzelfde team herleid.
    /// <para>
    /// <c>UPPER(...)</c>-vergelijkingen op de teamalias-tekst — zelfde reden als #820's andere
    /// teamresolutie-fixes: Postgres' default-collatie is case-sensitive, in tegenstelling tot SQL
    /// Server's <c>Latin1_General_CI_AS</c>.
    /// </para>
    /// <para>
    /// Bewust ONGUARD (geen try/catch) — zelfde als het origineel: een fout hier hoort de hele
    /// sync te laten falen, in tegenstelling tot de (wél best-effort) teamcanonicalisatie.
    /// </para>
    /// </summary>
    internal static async Task MarkeerVervallenGeplandeWedstrijdenAsync(
        string connectionString, string clubCode, ILogger log)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        string accommodatie;
        try
        {
            accommodatie = await PostgresClubScope.RequireAccommodatieAsync(conn, clubCode);
        }
        catch (InvalidOperationException)
        {
            log.LogWarning(
                "Instelling 'accommodatie' niet geconfigureerd — MarkeerVervallenGeplandeWedstrijden overgeslagen. " +
                "Stel de accommodatienaam in via Admin GUI → Instellingen.");
            return;
        }

        await using var cmd = new NpgsqlCommand($@"
            UPDATE planner.geplandewedstrijden gw
            SET isvervallen = TRUE,
                sportlinkwedstrijdcode = m.wedstrijdcode,
                mta_modified = NOW()
            FROM his.matches m
            WHERE m.kaledatum::date = gw.datum
              AND {PostgresClubScope.HisFilter("m")}
              AND EXISTS (
                  SELECT 1
                  FROM public.teamaliassen amatch
                  INNER JOIN public.teamaliassen aplanner
                      ON aplanner.teamid = amatch.teamid
                     AND aplanner.clubcode = amatch.clubcode
                     AND aplanner.status = 'validated'
                     AND UPPER(aplanner.ruwetekst) = UPPER(gw.teamnaam)
                  WHERE amatch.clubcode = {PostgresClubScope.ClubCodeParam}
                    AND amatch.status = 'validated'
                    AND UPPER(amatch.ruwetekst) = UPPER(m.teamnaam))
              AND gw.isvervallen = FALSE
              AND gw.status <> 'Geannuleerd'
              AND gw.clubcode = {PostgresClubScope.ClubCodeParam}
              AND m.accommodatie ILIKE @accommodatiepattern
        ", conn);
        cmd.Parameters.AddWithValue("accommodatiepattern", $"%{accommodatie}%");
        PostgresClubScope.AddHisParams(cmd, clubCode);
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows > 0)
            log.LogInformation("Post-sync: {Count} geplande wedstrijd(en) als vervallen gemarkeerd", rows);
    }
}
