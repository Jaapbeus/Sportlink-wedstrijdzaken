using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
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
internal static class TeamCanonicalisatieService
{
    private static string Cs => SystemUtilities.DatabaseConfig.ConnectionString;

    internal static async Task RefreshAsync(string clubCode, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(clubCode))
            throw new ArgumentException("ClubCode is verplicht voor teamcanonicalisatie.", nameof(clubCode));

        var rijen = await LoadHisTeamsAsync(clubCode);
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

        using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();

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

        // Bewust géén aliassen uit de sync: alle Sportlink-schrijfwijzen van één team normaliseren
        // per definitie naar dezelfde sleutel (daar is de groepering hierboven op gebaseerd), dus een
        // alias-rij zou exact dupliceren wat dbo.Teams al weet. dbo.TeamAliassen is uitsluitend voor
        // schrijfwijzen die NIET uit de normalisatie volgen: geleerd uit e-mail of handmatig
        // toegevoegd door de coördinator (#697/#701).
        log.LogInformation(
            "TEAMS CANONICALISATIE - {Teams} canonieke teams uit {Rijen} his.teams-rijen "
            + "({Gedeactiveerd} gedeactiveerd, {Fouten} overgeslagen) voor club {ClubCode}",
            teams, rijen.Count, gedeactiveerd, fouten, clubCode);
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
