using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Planner.Shared;
using SportlinkFunction.Planner;

namespace SportlinkFunction.TeamResolution;

/// <summary>
/// Vult <c>dbo.Teams</c>/<c>dbo.TeamAliassen</c> vanuit <c>his.teams</c> na elke Sportlink-sync (#696).
/// Draait ná de <c>stg→his</c>-merge voor teams in <see cref="SportlinkSyncPipeline"/>.
///
/// <para>
/// <b>Ontdubbeling is de kern van deze stap.</b> <c>his.teams</c> bevat elk team meerdere keren:
/// één rij per poule/competitiesoort, én in twee schrijfwijzen (lokale notatie <c>JO10-1</c> en
/// KNVB-notatie <c>[club] O10-1</c>). Beide verwijzen naar hetzelfde fysieke team maar hebben geen
/// gedeelde sleutel. Door te groeperen op de genormaliseerde sleutel uit
/// <see cref="TeamNaamNormalisatie"/> blijft er precies één canoniek team over, met alle
/// aangetroffen schrijfwijzen als gevalideerde alias.
/// </para>
/// </summary>
public static class TeamCanonicalisatieService
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    private const string BronSync = "Sync";
    private const string StatusValidated = "validated";

    /// <summary>
    /// Uitkomst van een canonicalisatieronde (#946). De sleutelmigratie draait ALTIJD als eerste stap
    /// binnen <c>RefreshAsync</c>; deze tellingen komen daaruit. Ze worden teruggegeven in plaats van
    /// alleen gelogd, zodat het herstelendpoint kan laten zien of er werkelijk iets hersteld is —
    /// anders is "hersteld" voor een beheerder niet te onderscheiden van "er gebeurde niets".
    /// <para>
    /// Alles nul betekent: de canonicalisatie is overgeslagen omdat er geen bronrijen waren.
    /// </para>
    /// </summary>
    public readonly record struct CanonicalisatieResultaat(
        int Teams, int SleutelsBijgewerkt, int DubbelenOpgeruimd);

    public static async Task<CanonicalisatieResultaat> RefreshAsync(string clubCode, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(clubCode))
            throw new ArgumentException("ClubCode is verplicht voor teamcanonicalisatie.", nameof(clubCode));

        var rijen = await LoadHisTeamsAsync(clubCode);
        if (rijen.Count == 0)
        {
            log.LogWarning("TEAMS CANONICALISATIE - geen rijen in his.teams voor club {ClubCode} — overgeslagen", clubCode);
            return default;
        }

        // Groepeer op genormaliseerde sleutel: dit is de ontdubbelingsstap.
        var groepen = GroepeerOpSleutel(rijen, clubCode);

        using var conn = new SqlConnection(Cs);
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

        return new CanonicalisatieResultaat(teams, sleutelsBijgewerkt, dubbelenOpgeruimd);
    }

    private static Dictionary<string, List<HisTeamRow>> GroepeerOpSleutel(List<HisTeamRow> rijen, string clubCode)
    {
        var groepen = new Dictionary<string, List<HisTeamRow>>(StringComparer.Ordinal);
        foreach (var rij in rijen)
        {
            var sleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(rij.Teamnaam, clubCode);
            if (sleutel.Length == 0) continue;

            if (!groepen.TryGetValue(sleutel, out var lijst))
                groepen[sleutel] = lijst = [];
            lijst.Add(rij);
        }
        return groepen;
    }

    /// <summary>
    /// Losse ingang voor de sleutelmigratie, voor paden die geen volledige canonicalisatie nodig
    /// hebben (zie <see cref="TeamlijstGereedheid"/>). Idempotent en goedkoop: zonder drift twee
    /// SELECTs en geen enkele wijziging.
    /// </summary>
    public static async Task<(int SleutelsBijgewerkt, int DubbelenOpgeruimd)> MigreerSleuteldriftAsync(
        string clubCode, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(clubCode))
            throw new ArgumentException("ClubCode is verplicht voor de sleutelmigratie.", nameof(clubCode));

        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        return await MigreerSleuteldriftAsync(conn, clubCode, log);
    }

    /// <summary>
    /// Brengt de opgeslagen genormaliseerde sleutels in lijn met de huidige regels van
    /// <see cref="TeamNaamNormalisatie"/> (#766). Idempotent: zonder drift doet deze stap niets.
    ///
    /// <para>
    /// <b>Waarom dit moet bestaan.</b> <c>TeamnaamGenormaliseerd</c> is persistent, maar wordt door
    /// C#-code berekend. Verandert een normalisatieregel, dan wijst de MERGE in
    /// <see cref="UpsertTeamAsync"/> (die op ClubCode + sleutel matcht) de bestaande rij niet meer
    /// aan en valt hij in de INSERT-tak — waar hij botst op <c>UQ_Teams_Club_Teamnaam</c>, want de
    /// teamnaam bestaat al. Die fout wordt per team gevangen en gelogd, terwijl
    /// <see cref="DeactiveerOntbrekendeTeamsAsync"/> de oude rij op <c>IsActief = 0</c> zet. Netto
    /// resultaat zonder deze migratiestap: de teams verdwijnen uit <c>dbo.Teams</c> en komen ook bij
    /// een volgende sync nooit terug, omdat de unique constraint blijft falen.
    /// </para>
    /// <para>
    /// Door de sleutel te herberekenen uit de al opgeslagen <c>Teamnaam</c> herstelt elke club zich
    /// bij de eerstvolgende sync automatisch, zonder handmatig migratiescript en zonder de
    /// normalisatieregels in T-SQL na te bouwen (wat de architectuurregel "één vertaalpunt" zou
    /// breken).
    /// </para>
    /// <para>
    /// Vallen twee bestaande rijen na herberekening op dezelfde sleutel, dan waren het twee
    /// schrijfwijzen van hetzelfde fysieke team. De rij die de sleutel al had (of anders de oudste)
    /// blijft bestaan; de aliassen van de ander worden naar die winnaar omgehangen en de dubbele rij
    /// wordt verwijderd. Verwijderen mag hier — de reden om normaal te deactiveren is dat
    /// <c>dbo.TeamAliassen</c> ernaar verwijst, en die verwijzingen zijn dan net omgehangen. Laten
    /// staan zou juist schadelijk zijn: de rij houdt de teamnaam bezet en blokkeert daarmee de
    /// upsert van de winnaar.
    /// </para>
    /// </summary>
    private static async Task<(int SleutelsBijgewerkt, int DubbelenOpgeruimd)> MigreerSleuteldriftAsync(
        SqlConnection conn, string clubCode, ILogger log)
    {
        var rijen = await LaadTeamsMetSleutelsAsync(conn, clubCode);
        var doelen = BerekenSleutelDoelen(rijen, clubCode);

        if (doelen.All(IsOngewijzigd))
            return (0, 0);

        int bijgewerkt = 0, opgeruimd = 0;

        foreach (var groep in doelen.GroupBy(r => r.NieuweSleutel, StringComparer.Ordinal))
        {
            var (b, o) = await VerwerkSleutelGroepAsync(conn, clubCode, groep, log);
            bijgewerkt += b;
            opgeruimd += o;
        }

        // Aliassen met Bron='Sync' worden verderop toch bijgewerkt, maar geleerde en handmatig
        // toegevoegde aliassen niet. Zonder deze stap blijft hun genormaliseerde kolom naar de oude
        // regels verwijzen en vindt de resolver ze alleen nog op de exacte ruwe tekst.
        var aliasBijgewerkt = await MigreerAliasSleutelsAsync(conn, clubCode);
        if (aliasBijgewerkt > 0)
            log.LogInformation("TEAMS CANONICALISATIE - {Aantal} aliassleutels gemigreerd", aliasBijgewerkt);

        return (bijgewerkt, opgeruimd);
    }

    private static async Task<List<(int TeamId, string Teamnaam, string OudeSleutel, int? OudeLeeftijd, int? OudTeamNummer)>> LaadTeamsMetSleutelsAsync(
        SqlConnection conn, string clubCode)
    {
        var rijen = new List<(int TeamId, string Teamnaam, string OudeSleutel, int? OudeLeeftijd, int? OudTeamNummer)>();
        using (var cmd = new SqlCommand(@"
            SELECT [TeamId], [Teamnaam], [TeamnaamGenormaliseerd], [LeeftijdNummer], [TeamNummer]
              FROM [dbo].[Teams] WHERE [ClubCode] = @clubCode", conn))
        {
            cmd.Parameters.AddWithValue("@clubCode", clubCode);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rijen.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                           reader.IsDBNull(3) ? null : reader.GetInt32(3),
                           reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }
        return rijen;
    }

    private static List<(int TeamId, string Teamnaam, string OudeSleutel, int? OudeLeeftijd, int? OudTeamNummer, string NieuweSleutel, int? NieuweLeeftijd, int? NieuwTeamNummer)> BerekenSleutelDoelen(
        List<(int TeamId, string Teamnaam, string OudeSleutel, int? OudeLeeftijd, int? OudTeamNummer)> rijen, string clubCode)
    {
        // LeeftijdNummer/TeamNummer worden hier meegenomen omdat ze uit dezelfde normalisatie komen:
        // een sleutel zonder streepje leverde geen ontleding op, dus stonden ze op NULL — en dan geeft
        // FindKandidatenAsync nul kandidaten en valt het hele kandidaten-/disambiguatiepad stil. Alleen
        // de sleutel repareren zou de exacte match herstellen en die ambiguïteitsafhandeling stil laten
        // liggen tot de volgende volledige canonicalisatie.
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

        return doelen;
    }

    private static bool IsOngewijzigd((int TeamId, string Teamnaam, string OudeSleutel, int? OudeLeeftijd,
        int? OudTeamNummer, string NieuweSleutel, int? NieuweLeeftijd, int? NieuwTeamNummer) r)
        => r.OudeSleutel == r.NieuweSleutel
           && r.OudeLeeftijd == r.NieuweLeeftijd
           && r.OudTeamNummer == r.NieuwTeamNummer;

    private static async Task<(int Bijgewerkt, int Opgeruimd)> VerwerkSleutelGroepAsync(
        SqlConnection conn, string clubCode,
        IGrouping<string, (int TeamId, string Teamnaam, string OudeSleutel, int? OudeLeeftijd, int? OudTeamNummer, string NieuweSleutel, int? NieuweLeeftijd, int? NieuwTeamNummer)> groep,
        ILogger log)
    {
        int bijgewerkt = 0, opgeruimd = 0;

        // De rij die deze sleutel al heeft is de winnaar: dan hoeft er niets te verschuiven.
        // Anders de oudste rij, zodat de uitkomst deterministisch is.
        var kandidaten = groep.ToList();
        var winnaar = kandidaten.FirstOrDefault(
            r => r.OudeSleutel == r.NieuweSleutel,
            kandidaten.OrderBy(r => r.TeamId).First());

        foreach (var verliezer in kandidaten.Where(r => r.TeamId != winnaar.TeamId))
        {
            await HangAliassenOmAsync(conn, clubCode, verliezer.TeamId, winnaar.TeamId);
            using var deleteCmd = new SqlCommand(
                "DELETE FROM [dbo].[Teams] WHERE [TeamId] = @teamId", conn);
            deleteCmd.Parameters.AddWithValue("@teamId", verliezer.TeamId);
            await deleteCmd.ExecuteNonQueryAsync();
            opgeruimd++;

            log.LogInformation(
                "TEAMS CANONICALISATIE - dubbele schrijfwijze '{Dubbel}' samengevoegd met '{Winnaar}' "
                + "(sleutel {Sleutel})", verliezer.Teamnaam, winnaar.Teamnaam, winnaar.NieuweSleutel);
        }

        if (IsOngewijzigd(winnaar))
            return (bijgewerkt, opgeruimd);

        using var updateCmd = new SqlCommand(@"
            UPDATE [dbo].[Teams]
               SET [TeamnaamGenormaliseerd] = @nieuweSleutel,
                   [LeeftijdNummer]         = @leeftijdNummer,
                   [TeamNummer]             = @teamNummer,
                   [mta_modified]           = GETUTCDATE()
             WHERE [TeamId] = @teamId", conn);
        updateCmd.Parameters.AddWithValue("@nieuweSleutel", winnaar.NieuweSleutel);
        updateCmd.Parameters.AddWithValue("@leeftijdNummer", (object?)winnaar.NieuweLeeftijd ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@teamNummer", (object?)winnaar.NieuwTeamNummer ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@teamId", winnaar.TeamId);
        await updateCmd.ExecuteNonQueryAsync();
        bijgewerkt++;

        log.LogInformation(
            "TEAMS CANONICALISATIE - sleutel gemigreerd voor '{Teamnaam}': {Oud} → {Nieuw} "
            + "(leeftijd={Leeftijd}, teamnummer={TeamNummer})",
            winnaar.Teamnaam, winnaar.OudeSleutel, winnaar.NieuweSleutel,
            winnaar.NieuweLeeftijd, winnaar.NieuwTeamNummer);

        return (bijgewerkt, opgeruimd);
    }

    private static async Task HangAliassenOmAsync(
        SqlConnection conn, string clubCode, int vanTeamId, int naarTeamId)
    {
        // Een alias die al naar de winnaar wijst zou de unieke (ClubCode, RuweTekst) schenden; die
        // kan weg, want hij is dan letterlijk dubbel.
        using var cmd = new SqlCommand(@"
            DELETE FROM [dbo].[TeamAliassen]
             WHERE [ClubCode] = @clubCode AND [TeamId] = @vanTeamId
               AND EXISTS (SELECT 1 FROM [dbo].[TeamAliassen] b
                            WHERE b.[ClubCode] = @clubCode AND b.[TeamId] = @naarTeamId
                              AND b.[RuweTekst] = [dbo].[TeamAliassen].[RuweTekst]);

            UPDATE [dbo].[TeamAliassen]
               SET [TeamId] = @naarTeamId, [mta_modified] = GETUTCDATE()
             WHERE [ClubCode] = @clubCode AND [TeamId] = @vanTeamId;", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        cmd.Parameters.AddWithValue("@vanTeamId", vanTeamId);
        cmd.Parameters.AddWithValue("@naarTeamId", naarTeamId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> MigreerAliasSleutelsAsync(SqlConnection conn, string clubCode)
    {
        var aliassen = new List<(int Id, string RuweTekst, string OudeSleutel)>();
        using (var cmd = new SqlCommand(
            "SELECT [Id], [RuweTekst], [RuweTekstGenormaliseerd] FROM [dbo].[TeamAliassen] WHERE [ClubCode] = @clubCode", conn))
        {
            cmd.Parameters.AddWithValue("@clubCode", clubCode);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                aliassen.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        int bijgewerkt = 0;
        foreach (var (id, ruweTekst, oudeSleutel) in aliassen)
        {
            var nieuweSleutel = TeamNaamNormalisatie.NormaliseerVoorVergelijking(ruweTekst, clubCode);
            if (nieuweSleutel.Length == 0 || nieuweSleutel == oudeSleutel) continue;

            using var cmd = new SqlCommand(@"
                UPDATE [dbo].[TeamAliassen]
                   SET [RuweTekstGenormaliseerd] = @nieuweSleutel, [mta_modified] = GETUTCDATE()
                 WHERE [Id] = @id", conn);
            cmd.Parameters.AddWithValue("@nieuweSleutel", nieuweSleutel);
            cmd.Parameters.AddWithValue("@id", id);
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
    /// normalisatie die dat oplost leeft in C# — niet in T-SQL.
    /// </para>
    /// <para>
    /// Door hier elke bronschrijfwijze één keer te herleiden en op te slaan, wordt het zoeken van een
    /// wedstrijd een <b>exacte</b> join op de ruwe naam. Dat is niet alleen sneller dan een
    /// <c>LIKE</c>-patroon, het sluit ook de klasse fouten uit waarbij "JO13-1" ook "JO13-10" raakt.
    /// </para>
    /// <para>
    /// Schrijfwijzen die niet herleid kunnen worden zijn in de praktijk géén clubteams — losse
    /// toernooi-inschrijvingen en tegenstanders in oefenwedstrijden. Die krijgen bewust geen alias en
    /// worden alleen geteld, zodat een onverwachte stijging opvalt zonder de review-lijst te vervuilen.
    /// </para>
    /// </summary>
    private static async Task<(int Gekoppeld, int Onbekend)> RegistreerBronSchrijfwijzenAsync(
        SqlConnection conn, string clubCode, ILogger log)
    {
        var schrijfwijzen = new List<string>();
        using (var cmd = new SqlCommand(@"
            SELECT [teamnaam] FROM (
                SELECT DISTINCT [teamnaam] FROM [his].[matches]
                WHERE [ClubCode] = @clubCode AND [mta_deleted] IS NULL
                UNION
                SELECT DISTINCT [teamnaam] FROM [his].[teams]
                WHERE [ClubCode] = @clubCode AND [mta_deleted] IS NULL
            ) AS bron
            WHERE [teamnaam] IS NOT NULL AND LTRIM(RTRIM([teamnaam])) <> ''
        ", conn))
        {
            cmd.Parameters.AddWithValue("@clubCode", clubCode);
            using var reader = await cmd.ExecuteReaderAsync();
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
    /// </summary>
    private static async Task<bool> UpsertBronAliasAsync(
        SqlConnection conn, string clubCode, string ruweTekst, string sleutel)
    {
        using var cmd = new SqlCommand($@"
            DECLARE @teamId INT = (
                SELECT TOP 1 [TeamId] FROM [dbo].[Teams]
                WHERE [ClubCode] = @clubCode AND [TeamnaamGenormaliseerd] = @sleutel AND [IsActief] = 1);

            IF @teamId IS NULL
            BEGIN
                SELECT CAST(0 AS BIT);
                RETURN;
            END

            MERGE [dbo].[TeamAliassen] AS target
            USING (SELECT @clubCode AS ClubCode, @ruweTekst AS RuweTekst) AS src
                ON target.[ClubCode] = src.[ClubCode] AND target.[RuweTekst] = src.[RuweTekst]
            WHEN MATCHED AND target.[Bron] = '{BronSync}' THEN
                UPDATE SET [TeamId] = @teamId,
                           [RuweTekstGenormaliseerd] = @sleutel,
                           [Status] = '{StatusValidated}',
                           [mta_modified] = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT ([ClubCode], [RuweTekst], [RuweTekstGenormaliseerd], [TeamId], [Bron], [Status])
                VALUES (@clubCode, @ruweTekst, @sleutel, @teamId, '{BronSync}', '{StatusValidated}');

            SELECT CAST(1 AS BIT);
        ", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        cmd.Parameters.AddWithValue("@ruweTekst", ruweTekst);
        cmd.Parameters.AddWithValue("@sleutel", sleutel);

        var resultaat = await cmd.ExecuteScalarAsync();
        return resultaat is bool ok && ok;
    }

    private static async Task<List<HisTeamRow>> LoadHisTeamsAsync(string clubCode)
    {
        var resultaten = new List<HisTeamRow>();
        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT
                [teamnaam],
                MAX([bk_teams])            AS BkTeams,
                MAX([leeftijdscategorie])  AS LeeftijdsCategorie,
                MAX([teamsoort])           AS Teamsoort
            FROM [his].[teams]
            WHERE [ClubCode] = @clubCode
              AND [mta_deleted] IS NULL
              AND [teamnaam] IS NOT NULL
              AND LTRIM(RTRIM([teamnaam])) <> ''
            GROUP BY [teamnaam]
        ", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        using var reader = await cmd.ExecuteReaderAsync();
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
        SqlConnection conn, string clubCode, string sleutel, List<HisTeamRow> groep)
    {
        // Bondsnotatie heeft voorkeur als canonieke weergavenaam: die vorm staat ook in
        // his.matches.wedstrijd en is wat tegenstanders gebruiken. Anders de lokale naam.
        var bond = groep.FirstOrDefault(r => string.Equals(r.Teamsoort, "bond", StringComparison.OrdinalIgnoreCase));
        var gekozen = bond ?? groep[0];

        var componenten = TeamNaamNormalisatie.Parse(gekozen.Teamnaam, clubCode);
        var leeftijdsCategorie = LeeftijdNormalisatie.Normaliseer(
            groep.Select(r => r.LeeftijdsCategorie).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)));

        using var cmd = new SqlCommand(@"
            MERGE [dbo].[Teams] AS target
            USING (SELECT @clubCode AS ClubCode, @sleutel AS TeamnaamGenormaliseerd) AS src
                ON target.[ClubCode] = src.[ClubCode]
               AND target.[TeamnaamGenormaliseerd] = src.[TeamnaamGenormaliseerd]
            WHEN MATCHED THEN
                UPDATE SET
                    [Teamnaam]           = @teamnaam,
                    [LeeftijdsCategorie] = @leeftijdsCategorie,
                    [LeeftijdNummer]     = @leeftijdNummer,
                    [TeamNummer]         = @teamNummer,
                    [BkTeams]            = @bkTeams,
                    [IsActief]           = 1,
                    [mta_modified]       = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT ([ClubCode], [Teamnaam], [TeamnaamGenormaliseerd], [LeeftijdsCategorie],
                        [LeeftijdNummer], [TeamNummer], [BkTeams], [IsActief])
                VALUES (@clubCode, @teamnaam, @sleutel, @leeftijdsCategorie,
                        @leeftijdNummer, @teamNummer, @bkTeams, 1);
        ", conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        cmd.Parameters.AddWithValue("@teamnaam", gekozen.Teamnaam);
        cmd.Parameters.AddWithValue("@sleutel", sleutel);
        cmd.Parameters.AddWithValue("@leeftijdsCategorie",
            string.IsNullOrEmpty(leeftijdsCategorie) ? DBNull.Value : leeftijdsCategorie);
        cmd.Parameters.AddWithValue("@leeftijdNummer", (object?)componenten?.LeeftijdNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@teamNummer", (object?)componenten?.TeamNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bkTeams", (object?)gekozen.BkTeams ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Teams die niet meer in de huidige his.teams-set voorkomen worden gedeactiveerd, niet
    /// verwijderd — TeamAliassen verwijst er nog naar en de historie blijft opvraagbaar.
    /// </summary>
    private static async Task<int> DeactiveerOntbrekendeTeamsAsync(
        SqlConnection conn, string clubCode, IEnumerable<string> actueleSleutels)
    {
        var actueel = new HashSet<string>(actueleSleutels, StringComparer.Ordinal);
        var teDeactiveren = new List<int>();

        using (var cmd = new SqlCommand(
            "SELECT [TeamId], [TeamnaamGenormaliseerd] FROM [dbo].[Teams] WHERE [ClubCode] = @clubCode AND [IsActief] = 1", conn))
        {
            cmd.Parameters.AddWithValue("@clubCode", clubCode);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!actueel.Contains(reader.GetString(1)))
                    teDeactiveren.Add(reader.GetInt32(0));
            }
        }

        foreach (var teamId in teDeactiveren)
        {
            using var updateCmd = new SqlCommand(
                "UPDATE [dbo].[Teams] SET [IsActief] = 0, [mta_modified] = GETUTCDATE() WHERE [TeamId] = @teamId", conn);
            updateCmd.Parameters.AddWithValue("@teamId", teamId);
            await updateCmd.ExecuteNonQueryAsync();
        }

        return teDeactiveren.Count;
    }

    private sealed record HisTeamRow(string Teamnaam, string? BkTeams, string? LeeftijdsCategorie, string? Teamsoort);
}
