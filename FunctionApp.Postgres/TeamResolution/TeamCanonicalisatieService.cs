using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared;

namespace FunctionApp.Postgres.TeamResolution;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/TeamResolution/TeamCanonicalisatieService.cs</c>
/// (#889). Vult <c>public.teams</c>/<c>public.teamaliassen</c> vanuit <c>his.teams</c> na elke
/// Sportlink-sync (#696); draait ná de <c>stg→his</c>-merge voor teams in
/// <see cref="Sync.PostgresSyncPipeline"/>.
///
/// <para>
/// <b>Ontdubbeling is de kern van deze stap.</b> <c>his.teams</c> bevat elk team meerdere keren:
/// één rij per poule/competitiesoort, én in twee schrijfwijzen (lokale notatie <c>JO10-1</c> en
/// KNVB-notatie <c>[club] O10-1</c>). Beide verwijzen naar hetzelfde fysieke team maar hebben geen
/// gedeelde sleutel. Door te groeperen op de genormaliseerde sleutel uit
/// <see cref="TeamNaamNormalisatie"/> blijft er precies één canoniek team over, met alle
/// aangetroffen schrijfwijzen als gevalideerde alias.
/// </para>
///
/// <para>
/// <b>Vertaalconstructies t.o.v. het SQL Server-origineel:</b>
/// <list type="bullet">
/// <item><c>MERGE ... WHEN MATCHED / WHEN NOT MATCHED</c> → <c>INSERT ... ON CONFLICT ... DO
/// UPDATE</c>. De inferentie-doelen zijn de <b>expression-based</b> unique indexes uit
/// <c>007_teams_collation_fix.sql</c> (#820): <c>(clubcode, upper(teamnaamgenormaliseerd))</c>
/// respectievelijk <c>(clubcode, upper(ruwetekst))</c> — niet de kale kolomparen, die bestaan daar
/// niet meer.</item>
/// <item>De <c>WHEN MATCHED AND target.[Bron] = 'Sync'</c>-conditie van de aliasupsert wordt de
/// <c>WHERE</c>-clausule van <c>DO UPDATE</c>: een handmatig of geleerd toegevoegde alias
/// (<c>Bron &lt;&gt; 'Sync'</c>) mag ook hier niet stilzwijgend door de sync overschreven worden.</item>
/// <item><c>DECLARE @teamId ... IF @teamId IS NULL ... RETURN</c> bestaat buiten een functie niet in
/// Postgres. De aliasupsert lost dat op met een CTE die nul rijen oplevert als er geen actief team
/// is — dan gebeurt er niets en meldt de C#-kant "niet herleidbaar", exact het gedrag van de
/// <c>RETURN</c>. Zelfde precedent als <c>TeamSchrijfwijzenAsync</c> (#888, sectie 25).</item>
/// <item><c>GETUTCDATE()</c> → <c>NOW()</c> (de kolommen zijn <c>TIMESTAMPTZ</c>, #854),
/// <c>LTRIM(RTRIM(...))</c> → <c>TRIM(...)</c>.</item>
/// <item>Sleutelvergelijkingen staan expliciet in <c>UPPER(...)</c> — #820: Postgres' default-
/// collatie is case-sensitive, dus zonder die wrap zou een historische rij met afwijkende casing
/// stilzwijgend niet matchen én zou de upsert in de INSERT-tak vallen. Zelfde regel als
/// <see cref="TeamCandidateRepository"/>.</item>
/// </list>
/// </para>
/// </summary>
internal static class TeamCanonicalisatieService
{
    internal static async Task RefreshAsync(string connectionString, string clubCode, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(clubCode))
            throw new ArgumentException("ClubCode is verplicht voor teamcanonicalisatie.", nameof(clubCode));

        var rijen = await LoadHisTeamsAsync(connectionString, clubCode);
        if (rijen.Count == 0)
        {
            log.LogWarning("TEAMS CANONICALISATIE - geen rijen in his.teams voor club {ClubCode} — overgeslagen", clubCode);
            return;
        }

        // Groepeer op genormaliseerde sleutel: dit is de ontdubbelingsstap.
        var groepen = new Dictionary<string, List<HisTeamRow>>(StringComparer.Ordinal);
        foreach (var rij in rijen)
        {
            var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(rij.Teamnaam, clubCode);
            if (sleutel.Length == 0) continue;

            if (!groepen.TryGetValue(sleutel, out var lijst))
                groepen[sleutel] = lijst = [];
            lijst.Add(rij);
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        // Eerst de opgeslagen sleutels in lijn brengen met de huidige normalisatieregels — zie
        // MigreerSleuteldriftAsync voor waarom dit vóór de upserts moet.
        var (sleutelsBijgewerkt, dubbelenOpgeruimd) = await MigreerSleuteldriftAsync(conn, clubCode, log);

        int teams = 0, fouten = 0;

        foreach (var (sleutel, groep) in groepen)
        {
            try
            {
                await UpsertTeamAsync(conn, clubCode, sleutel, groep);
                teams++;
            }
            catch (Exception ex)
            {
                // Eén onverwachte teamnaam mag nooit de hele canonicalisatie stilzetten.
                fouten++;
                log.LogError(ex, "TEAMS CANONICALISATIE - team met sleutel {Sleutel} overgeslagen", sleutel);
            }
        }

        var gedeactiveerd = await DeactiveerOntbrekendeTeamsAsync(conn, clubCode, groepen.Keys);
        var (aliassen, onbekend) = await RegistreerBronSchrijfwijzenAsync(conn, clubCode, log);

        log.LogInformation(
            "TEAMS CANONICALISATIE - {Teams} canonieke teams uit {Rijen} his.teams-rijen, "
            + "{Aliassen} bronschrijfwijzen gekoppeld, {Onbekend} niet herleidbaar "
            + "({Gedeactiveerd} gedeactiveerd, {Fouten} overgeslagen, {SleutelsBijgewerkt} sleutels "
            + "gemigreerd, {DubbelenOpgeruimd} dubbele schrijfwijzen samengevoegd) voor club {ClubCode}",
            teams, rijen.Count, aliassen, onbekend, gedeactiveerd, fouten,
            sleutelsBijgewerkt, dubbelenOpgeruimd, clubCode);
    }

    /// <summary>
    /// Brengt de opgeslagen genormaliseerde sleutels in lijn met de huidige regels van
    /// <see cref="TeamNaamNormalisatie"/> (#766). Idempotent: zonder drift doet deze stap niets.
    ///
    /// <para>
    /// <b>Waarom dit moet bestaan.</b> <c>teamnaamgenormaliseerd</c> is persistent, maar wordt door
    /// C#-code berekend. Verandert een normalisatieregel, dan matcht de upsert in
    /// <see cref="UpsertTeamAsync"/> (die op clubcode + sleutel matcht) de bestaande rij niet meer
    /// en valt hij in de INSERT-tak — waar hij botst op de unique index op
    /// <c>(clubcode, upper(teamnaam))</c>, want de teamnaam bestaat al. Die fout wordt per team
    /// gevangen en gelogd, terwijl <see cref="DeactiveerOntbrekendeTeamsAsync"/> de oude rij op
    /// <c>isactief = FALSE</c> zet. Netto resultaat zonder deze migratiestap: de teams verdwijnen
    /// uit <c>public.teams</c> en komen ook bij een volgende sync nooit terug.
    /// </para>
    /// </summary>
    private static async Task<(int SleutelsBijgewerkt, int DubbelenOpgeruimd)> MigreerSleuteldriftAsync(
        NpgsqlConnection conn, string clubCode, ILogger log)
    {
        var rijen = new List<(int TeamId, string Teamnaam, string OudeSleutel, int? OudeLeeftijd, int? OudTeamNummer)>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT teamid, teamnaam, teamnaamgenormaliseerd, leeftijdnummer, teamnummer
              FROM public.teams WHERE clubcode = @clubcode", conn))
        {
            cmd.Parameters.AddWithValue("clubcode", clubCode);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rijen.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                           reader.IsDBNull(3) ? null : reader.GetInt32(3),
                           reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }

        // LeeftijdNummer/TeamNummer worden hier meegenomen omdat ze uit dezelfde normalisatie komen:
        // een sleutel zonder streepje leverde geen ontleding op, dus stonden ze op NULL — en dan geeft
        // FindKandidatenAsync nul kandidaten en valt het hele kandidaten-/disambiguatiepad stil.
        var doelen = rijen
            .Select(r =>
            {
                var componenten = TeamNaamNormalisatie.Parse(r.Teamnaam, clubCode);
                return (r.TeamId, r.Teamnaam, r.OudeSleutel, r.OudeLeeftijd, r.OudTeamNummer,
                        NieuweSleutel: TeamNaamNormalisatie.NormaliseerVoorVergelijking(r.Teamnaam, clubCode),
                        NieuweLeeftijd: componenten?.LeeftijdNummer,
                        NieuwTeamNummer: componenten?.TeamNummer);
            })
            .Where(r => r.NieuweSleutel.Length > 0)
            .ToList();

        static bool IsOngewijzigd((int TeamId, string Teamnaam, string OudeSleutel, int? OudeLeeftijd,
            int? OudTeamNummer, string NieuweSleutel, int? NieuweLeeftijd, int? NieuwTeamNummer) r)
            => r.OudeSleutel == r.NieuweSleutel
               && r.OudeLeeftijd == r.NieuweLeeftijd
               && r.OudTeamNummer == r.NieuwTeamNummer;

        if (doelen.All(IsOngewijzigd))
            return (0, 0);

        int bijgewerkt = 0, opgeruimd = 0;

        foreach (var groep in doelen.GroupBy(r => r.NieuweSleutel, StringComparer.Ordinal))
        {
            // De rij die deze sleutel al heeft is de winnaar: dan hoeft er niets te verschuiven.
            // Anders de oudste rij, zodat de uitkomst deterministisch is.
            var kandidaten = groep.ToList();
            var winnaar = kandidaten.FirstOrDefault(
                r => r.OudeSleutel == r.NieuweSleutel,
                kandidaten.OrderBy(r => r.TeamId).First());

            foreach (var verliezer in kandidaten.Where(r => r.TeamId != winnaar.TeamId))
            {
                await HangAliassenOmAsync(conn, clubCode, verliezer.TeamId, winnaar.TeamId);
                await using var deleteCmd = new NpgsqlCommand(
                    "DELETE FROM public.teams WHERE teamid = @teamid", conn);
                deleteCmd.Parameters.AddWithValue("teamid", verliezer.TeamId);
                await deleteCmd.ExecuteNonQueryAsync();
                opgeruimd++;

                log.LogInformation(
                    "TEAMS CANONICALISATIE - dubbele schrijfwijze '{Dubbel}' samengevoegd met '{Winnaar}' "
                    + "(sleutel {Sleutel})", verliezer.Teamnaam, winnaar.Teamnaam, winnaar.NieuweSleutel);
            }

            if (IsOngewijzigd(winnaar)) continue;

            await using var updateCmd = new NpgsqlCommand(@"
                UPDATE public.teams
                   SET teamnaamgenormaliseerd = @nieuwesleutel,
                       leeftijdnummer         = @leeftijdnummer,
                       teamnummer             = @teamnummer,
                       mta_modified           = NOW()
                 WHERE teamid = @teamid", conn);
            updateCmd.Parameters.AddWithValue("nieuwesleutel", winnaar.NieuweSleutel);
            updateCmd.Parameters.AddWithValue("leeftijdnummer", (object?)winnaar.NieuweLeeftijd ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("teamnummer", (object?)winnaar.NieuwTeamNummer ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("teamid", winnaar.TeamId);
            await updateCmd.ExecuteNonQueryAsync();
            bijgewerkt++;

            log.LogInformation(
                "TEAMS CANONICALISATIE - sleutel gemigreerd voor '{Teamnaam}': {Oud} → {Nieuw} "
                + "(leeftijd={Leeftijd}, teamnummer={TeamNummer})",
                winnaar.Teamnaam, winnaar.OudeSleutel, winnaar.NieuweSleutel,
                winnaar.NieuweLeeftijd, winnaar.NieuwTeamNummer);
        }

        // Aliassen met bron='Sync' worden verderop toch bijgewerkt, maar geleerde en handmatig
        // toegevoegde aliassen niet. Zonder deze stap blijft hun genormaliseerde kolom naar de oude
        // regels verwijzen en vindt de resolver ze alleen nog op de exacte ruwe tekst.
        var aliasBijgewerkt = await MigreerAliasSleutelsAsync(conn, clubCode);
        if (aliasBijgewerkt > 0)
            log.LogInformation("TEAMS CANONICALISATIE - {Aantal} aliassleutels gemigreerd", aliasBijgewerkt);

        return (bijgewerkt, opgeruimd);
    }

    /// <summary>
    /// Het SQL Server-origineel doet dit als één batch met twee statements. Postgres kent geen
    /// batch-scheiding binnen één command-tekst met dezelfde parameters op deze manier, dus het zijn
    /// twee losse commando's in dezelfde volgorde — functioneel identiek.
    /// </summary>
    private static async Task HangAliassenOmAsync(
        NpgsqlConnection conn, string clubCode, int vanTeamId, int naarTeamId)
    {
        // Een alias die al naar de winnaar wijst zou de unique index op (clubcode, upper(ruwetekst))
        // schenden; die kan weg, want hij is dan letterlijk dubbel.
        await using (var deleteCmd = new NpgsqlCommand(@"
            DELETE FROM public.teamaliassen a
             WHERE a.clubcode = @clubcode AND a.teamid = @vanteamid
               AND EXISTS (SELECT 1 FROM public.teamaliassen b
                            WHERE b.clubcode = @clubcode AND b.teamid = @naarteamid
                              AND UPPER(b.ruwetekst) = UPPER(a.ruwetekst))", conn))
        {
            deleteCmd.Parameters.AddWithValue("clubcode", clubCode);
            deleteCmd.Parameters.AddWithValue("vanteamid", vanTeamId);
            deleteCmd.Parameters.AddWithValue("naarteamid", naarTeamId);
            await deleteCmd.ExecuteNonQueryAsync();
        }

        await using var updateCmd = new NpgsqlCommand(@"
            UPDATE public.teamaliassen
               SET teamid = @naarteamid, mta_modified = NOW()
             WHERE clubcode = @clubcode AND teamid = @vanteamid", conn);
        updateCmd.Parameters.AddWithValue("clubcode", clubCode);
        updateCmd.Parameters.AddWithValue("vanteamid", vanTeamId);
        updateCmd.Parameters.AddWithValue("naarteamid", naarTeamId);
        await updateCmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> MigreerAliasSleutelsAsync(NpgsqlConnection conn, string clubCode)
    {
        var aliassen = new List<(int Id, string RuweTekst, string OudeSleutel)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, ruwetekst, ruwetekstgenormaliseerd FROM public.teamaliassen WHERE clubcode = @clubcode", conn))
        {
            cmd.Parameters.AddWithValue("clubcode", clubCode);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                aliassen.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        int bijgewerkt = 0;
        foreach (var (id, ruweTekst, oudeSleutel) in aliassen)
        {
            var nieuweSleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(ruweTekst, clubCode);
            if (nieuweSleutel.Length == 0 || nieuweSleutel == oudeSleutel) continue;

            await using var cmd = new NpgsqlCommand(@"
                UPDATE public.teamaliassen
                   SET ruwetekstgenormaliseerd = @nieuwesleutel, mta_modified = NOW()
                 WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("nieuwesleutel", nieuweSleutel);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
            bijgewerkt++;
        }
        return bijgewerkt;
    }

    /// <summary>
    /// Koppelt elke schrijfwijze die in de brondata voorkomt aan zijn canonieke team, als
    /// gevalideerde alias (#700).
    ///
    /// <para>
    /// <b>Waarom dit nodig is.</b> De schrijfwijze in <c>his.matches.teamnaam</c> wijkt af van die in
    /// <c>his.teams</c>: matches gebruiken "[club] JO10-1" (mét J), de bondsrijen in his.teams
    /// "[club] O10-1" (zonder). Vergelijken op de ruwe string levert daardoor nul treffers, en de
    /// normalisatie die dat oplost leeft in C# — niet in SQL.
    /// </para>
    /// <para>
    /// Schrijfwijzen die niet herleid kunnen worden zijn in de praktijk géén clubteams — losse
    /// toernooi-inschrijvingen en tegenstanders in oefenwedstrijden. Die krijgen bewust geen alias en
    /// worden alleen geteld.
    /// </para>
    /// </summary>
    private static async Task<(int Gekoppeld, int Onbekend)> RegistreerBronSchrijfwijzenAsync(
        NpgsqlConnection conn, string clubCode, ILogger log)
    {
        var schrijfwijzen = new List<string>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT teamnaam FROM (
                SELECT DISTINCT teamnaam FROM his.matches
                WHERE clubcode = @clubcode AND mta_deleted IS NULL
                UNION
                SELECT DISTINCT teamnaam FROM his.teams
                WHERE clubcode = @clubcode AND mta_deleted IS NULL
            ) AS bron
            WHERE teamnaam IS NOT NULL AND TRIM(teamnaam) <> ''
        ", conn))
        {
            cmd.Parameters.AddWithValue("clubcode", clubCode);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                schrijfwijzen.Add(reader.GetString(0).Trim());
        }

        int gekoppeld = 0;
        var onbekend = new List<string>();

        foreach (var ruweTekst in schrijfwijzen)
        {
            var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(ruweTekst, clubCode);
            if (sleutel.Length == 0) continue;

            try
            {
                if (await UpsertBronAliasAsync(conn, clubCode, ruweTekst, sleutel))
                    gekoppeld++;
                else
                    onbekend.Add(ruweTekst);
            }
            catch (Exception ex)
            {
                onbekend.Add(ruweTekst);
                log.LogError(ex, "TEAMS CANONICALISATIE - bronschrijfwijze overgeslagen (sleutel {Sleutel})", sleutel);
            }
        }

        if (onbekend.Count > 0)
        {
            // Teamnamen zijn geen persoonsgegevens, maar de lijst wordt begrensd zodat een
            // onverwacht grote bronset de logs niet volloopt.
            log.LogInformation(
                "TEAMS CANONICALISATIE - {Aantal} schrijfwijzen niet herleidbaar tot een clubteam: {Voorbeelden}",
                onbekend.Count, string.Join(", ", onbekend.Take(15)));
        }

        return (gekoppeld, onbekend.Count);
    }

    /// <summary>
    /// Retourneert true als de schrijfwijze aan een actief canoniek team gekoppeld kon worden.
    /// Idempotent: bij een bestaande rij wordt alleen de koppeling bijgewerkt.
    ///
    /// <para>
    /// De <c>doel</c>-CTE vervangt het origineel se <c>DECLARE @teamId ... IF NULL RETURN</c>: vindt
    /// hij geen actief team, dan levert hij nul rijen, doet de <c>INSERT ... SELECT</c> niets en
    /// levert <c>RETURNING</c> niets op — waarna deze methode <c>false</c> teruggeeft. Dat is exact
    /// het gedrag van de <c>RETURN</c> in de T-SQL-batch.
    /// </para>
    /// <para>
    /// <c>RETURNING</c> vuurt alleen bij een daadwerkelijk uitgevoerde INSERT of DO UPDATE. De
    /// <c>WHERE</c> op <c>DO UPDATE</c> (alleen <c>bron = 'Sync'</c>) kan die update onderdrukken;
    /// dan levert <c>RETURNING</c> niets, terwijl de schrijfwijze wel degelijk aan een team
    /// gekoppeld is. Vandaar de tweede <c>SELECT</c>-tak in de <c>bestaand</c>-CTE: die telt zo'n
    /// bewust-niet-overschreven handmatige alias als "gekoppeld", niet als "onbekend" — hetzelfde
    /// resultaat als het origineel, dat na de MERGE onvoorwaardelijk <c>1</c> teruggaf zodra er een
    /// team gevonden was.
    /// </para>
    /// </summary>
    private static async Task<bool> UpsertBronAliasAsync(
        NpgsqlConnection conn, string clubCode, string ruweTekst, string sleutel)
    {
        await using var cmd = new NpgsqlCommand(@"
            WITH doel AS (
                SELECT teamid FROM public.teams
                 WHERE clubcode = @clubcode
                   AND UPPER(teamnaamgenormaliseerd) = UPPER(@sleutel)
                   AND isactief = TRUE
                 LIMIT 1
            ),
            geschreven AS (
                INSERT INTO public.teamaliassen
                    (clubcode, ruwetekst, ruwetekstgenormaliseerd, teamid, bron, status)
                SELECT @clubcode, @ruwetekst, @sleutel, doel.teamid, 'Sync', 'validated'
                  FROM doel
                ON CONFLICT (clubcode, upper(ruwetekst)) DO UPDATE
                    SET teamid                  = EXCLUDED.teamid,
                        ruwetekstgenormaliseerd = EXCLUDED.ruwetekstgenormaliseerd,
                        status                  = 'validated',
                        mta_modified            = NOW()
                    WHERE teamaliassen.bron = 'Sync'
                RETURNING 1
            ),
            bestaand AS (
                SELECT 1 FROM public.teamaliassen a, doel
                 WHERE a.clubcode = @clubcode AND UPPER(a.ruwetekst) = UPPER(@ruwetekst)
            )
            SELECT 1 FROM geschreven
            UNION ALL
            SELECT 1 FROM bestaand
            LIMIT 1
        ", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("ruwetekst", ruweTekst);
        cmd.Parameters.AddWithValue("sleutel", sleutel);

        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task<List<HisTeamRow>> LoadHisTeamsAsync(string connectionString, string clubCode)
    {
        var resultaten = new List<HisTeamRow>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                teamnaam,
                MAX(bk_teams)           AS bkteams,
                MAX(leeftijdscategorie) AS leeftijdscategorie,
                MAX(teamsoort)          AS teamsoort
            FROM his.teams
            WHERE clubcode = @clubcode
              AND mta_deleted IS NULL
              AND teamnaam IS NOT NULL
              AND TRIM(teamnaam) <> ''
            GROUP BY teamnaam
        ", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            resultaten.Add(new HisTeamRow(
                reader.GetString(0).Trim(),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return resultaten;
    }

    private static async Task UpsertTeamAsync(
        NpgsqlConnection conn, string clubCode, string sleutel, List<HisTeamRow> groep)
    {
        // Bondsnotatie heeft voorkeur als canonieke weergavenaam: die vorm staat ook in
        // his.matches.wedstrijd en is wat tegenstanders gebruiken. Anders de lokale naam.
        var bond = groep.FirstOrDefault(r => string.Equals(r.Teamsoort, "bond", StringComparison.OrdinalIgnoreCase));
        var gekozen = bond ?? groep[0];

        var componenten = TeamNaamNormalisatie.Parse(gekozen.Teamnaam, clubCode);
        var leeftijdsCategorie = LeeftijdNormalisatie.Normaliseer(
            groep.Select(r => r.LeeftijdsCategorie).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)));

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.teams
                (clubcode, teamnaam, teamnaamgenormaliseerd, leeftijdscategorie,
                 leeftijdnummer, teamnummer, bkteams, isactief)
            VALUES (@clubcode, @teamnaam, @sleutel, @leeftijdscategorie,
                    @leeftijdnummer, @teamnummer, @bkteams, TRUE)
            ON CONFLICT (clubcode, upper(teamnaamgenormaliseerd)) DO UPDATE
                SET teamnaam           = EXCLUDED.teamnaam,
                    leeftijdscategorie = EXCLUDED.leeftijdscategorie,
                    leeftijdnummer     = EXCLUDED.leeftijdnummer,
                    teamnummer         = EXCLUDED.teamnummer,
                    bkteams            = EXCLUDED.bkteams,
                    isactief           = TRUE,
                    mta_modified       = NOW()
        ", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        cmd.Parameters.AddWithValue("teamnaam", gekozen.Teamnaam);
        cmd.Parameters.AddWithValue("sleutel", sleutel);
        cmd.Parameters.AddWithValue("leeftijdscategorie",
            string.IsNullOrEmpty(leeftijdsCategorie) ? DBNull.Value : leeftijdsCategorie);
        cmd.Parameters.AddWithValue("leeftijdnummer", (object?)componenten?.LeeftijdNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("teamnummer", (object?)componenten?.TeamNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bkteams", (object?)gekozen.BkTeams ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Teams die niet meer in de huidige his.teams-set voorkomen worden gedeactiveerd, niet
    /// verwijderd — public.teamaliassen verwijst er nog naar en de historie blijft opvraagbaar.
    /// </summary>
    private static async Task<int> DeactiveerOntbrekendeTeamsAsync(
        NpgsqlConnection conn, string clubCode, IEnumerable<string> actueleSleutels)
    {
        var actueel = new HashSet<string>(actueleSleutels, StringComparer.Ordinal);
        var teDeactiveren = new List<int>();

        await using (var cmd = new NpgsqlCommand(
            "SELECT teamid, teamnaamgenormaliseerd FROM public.teams WHERE clubcode = @clubcode AND isactief = TRUE", conn))
        {
            cmd.Parameters.AddWithValue("clubcode", clubCode);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!actueel.Contains(reader.GetString(1)))
                    teDeactiveren.Add(reader.GetInt32(0));
            }
        }

        foreach (var teamId in teDeactiveren)
        {
            await using var updateCmd = new NpgsqlCommand(
                "UPDATE public.teams SET isactief = FALSE, mta_modified = NOW() WHERE teamid = @teamid", conn);
            updateCmd.Parameters.AddWithValue("teamid", teamId);
            await updateCmd.ExecuteNonQueryAsync();
        }

        return teDeactiveren.Count;
    }

    private sealed record HisTeamRow(string Teamnaam, string? BkTeams, string? LeeftijdsCategorie, string? Teamsoort);
}
