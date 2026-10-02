using Microsoft.Data.SqlClient;
using Planner.Shared.Integrations.SportlinkClub;
using static Planner.Endpoints.Sportlink.ClubMatchEndpointCore;
using SportlinkFunction.Planner;
using SportlinkFunction.TeamResolution;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Sportlink/SportlinkClubMatchRepository.cs</c>
/// (#1266, epic #986): de leesqueries voor het oefenwedstrijd-formulier (#1116). Teams en velden
/// komen uit onze eigen database, niet uit Sportlink-picklists.
/// <para>
/// Bevat bewust géén eigen teamnaam-logica: de koppeling lokale ↔ KNVB-schrijfwijze loopt
/// uitsluitend via de aliastabel die <see cref="TeamCanonicalisatieService"/> vult
/// (docs/ARCHITECTUUR-TEAMRESOLUTIE.md, regel 1) — een eigen regex hier zou een
/// architectuurschending zijn. Het resultaattype <see cref="ClubMatchTeamKoppeling"/> is gedeeld
/// met de Postgres-tier (Planner.Shared); alleen de query verschilt, conform
/// docs/ARCHITECTUUR-DATABASE-TIERS.md (geen runtime-providerabstractie).
/// </para>
/// </summary>
internal static class SportlinkClubMatchRepository
{
    /// <summary>Alleen een gevalideerde alias telt als waarheid (regel 4 van
    /// docs/ARCHITECTUUR-TEAMRESOLUTIE.md); zelfde literal als de rest van deze tier
    /// (<c>TeamCandidateRepository</c>, <c>PlannerMatchRepository</c>).</summary>
    private const string StatusValidated = "validated";

    /// <summary>
    /// Zoekt een actief clubteam op naam (hoofdletterongevoelig) en haalt het Sportlink-team-ID
    /// erbij via de gevalideerde aliassen → <c>his.teams.teamcode</c>. Geeft <c>null</c> als het
    /// team niet bestaat of niet actief is — de aanroeper vertaalt dat naar een 400.
    /// </summary>
    internal static async Task<ClubMatchTeamKoppeling?> GetTeamKoppelingAsync(string clubCode, string teamNaam, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        // CROSS APPLY is het SQL Server-equivalent van Postgres' CROSS JOIN LATERAL: de subquery
        // mag naar p.[TeamId] van de buitenste rij verwijzen.
        using var cmd = new SqlCommand($@"
            SELECT TOP 1 p.[Teamnaam], p.[LeeftijdsCategorie], k.[SportlinkTeamId], k.[Aantal]
            FROM [dbo].[Teams] p
            CROSS APPLY (
                SELECT CAST(COUNT(DISTINCT h.[teamcode]) AS INT) AS [Aantal],
                       CASE WHEN COUNT(DISTINCT h.[teamcode]) = 1 THEN MIN(h.[teamcode]) END AS [SportlinkTeamId]
                FROM [dbo].[TeamAliassen] a
                INNER JOIN [his].[teams] h
                    ON {ClubScope.HisFilter("h")}
                   AND UPPER(h.[teamnaam]) = UPPER(a.[RuweTekst])
                   AND h.[mta_deleted] IS NULL
                   AND h.[teamcode] <> -1
                WHERE a.[TeamId] = p.[TeamId]
                  AND a.[ClubCode] = {ClubScope.ClubCodeParam}
                  AND a.[Status] = '{StatusValidated}'
            ) k
            WHERE p.[ClubCode] = {ClubScope.ClubCodeParam}
              AND p.[IsActief] = 1
              AND UPPER(p.[Teamnaam]) = UPPER(@Naam)", conn);
        ClubScope.AddHisParams(cmd, clubCode);
        cmd.Parameters.AddWithValue("@Naam", teamNaam.Trim());

        return (await LeesAlleAsync(cmd, TeamKoppelingRij)).FirstOrDefault();
    }

    /// <summary>Naam van een actief veld van deze club, of <c>null</c> als het veldnummer onbekend/inactief is.</summary>
    internal static async Task<string?> GetActiefVeldNaamAsync(string clubCode, int veldNummer, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT [VeldNaam] FROM [dbo].[Velden]
            WHERE [ClubCode] = {ClubScope.ClubCodeParam} AND [VeldNummer] = @VeldNummer AND [Actief] = 1", conn);
        ClubScope.AddClubParam(cmd, clubCode);
        cmd.Parameters.AddWithValue("@VeldNummer", veldNummer);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>Alle actieve velden van deze club (#1339) — bron voor de veld-dropdown in
    /// <c>SportlinkMatchPanel</c>, zodat de beheerder een veld op onze eigen naam kiest in plaats
    /// van Sportlinks <c>FieldId</c>-formaat te moeten kennen.</summary>
    internal static async Task<List<(int VeldNummer, string VeldNaam)>> GetActieveVeldenAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT [VeldNummer], [VeldNaam] FROM [dbo].[Velden]
            WHERE [ClubCode] = {ClubScope.ClubCodeParam} AND [Actief] = 1
            ORDER BY [VeldNummer]", conn);
        ClubScope.AddClubParam(cmd, clubCode);
        return await LeesAlleAsync(cmd, r => (r.GetInt32(0), r.GetString(1)));
    }

    /// <summary>Actieve teams (met leeftijdscategorie) én speeltijden van deze club (#1437) — bron voor de
    /// voorinvulling van "Wedstrijd aanmaken". Geen teamnaam-logica: de koppeling met de speeltijden
    /// gebeurt in <c>ClubMatchEndpointCore</c> (Planner.Shared-normalisatie).</summary>
    internal static async Task<(List<ClubMatchFormulierTeamInvoer> Teams, List<ClubMatchSpeeltijdInvoer> Speeltijden)> GetFormulierGegevensAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var teamsCmd = new SqlCommand($@"
            SELECT [Teamnaam], [LeeftijdsCategorie] FROM [dbo].[Teams]
            WHERE [ClubCode] = {ClubScope.ClubCodeParam} AND [IsActief] = 1 ORDER BY [Teamnaam]", conn);
        ClubScope.AddClubParam(teamsCmd, clubCode);
        using var tijdenCmd = new SqlCommand($@"
            SELECT [Leeftijd], [Veldafmeting], [WedstrijdTotaal] FROM [dbo].[Speeltijden]
            WHERE [ClubCode] = {ClubScope.ClubCodeParam}", conn);
        ClubScope.AddClubParam(tijdenCmd, clubCode);
        return (await LeesAlleAsync(teamsCmd, TeamRij), await LeesAlleAsync(tijdenCmd, SpeeltijdRij));
    }

    /// <summary>Clubinstelling Spelactiviteit (#1437, kolom via PostDeployment) — leeg of ontbrekend is <c>null</c>.</summary>
    internal static async Task<string?> GetSpelactiviteitAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT TOP 1 [SportlinkSpelactiviteit] FROM [dbo].[AppSettings]
            WHERE [ClubCode] = {ClubScope.ClubCodeParam}", conn);
        ClubScope.AddClubParam(cmd, clubCode);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>
    /// Reserveert het volgende volgnummer voor deze speeldag (#1437), atomair: <c>MERGE ... WITH
    /// (HOLDLOCK)</c> neemt een range-lock, dus twee gelijktijdige aanvragen krijgen nooit hetzelfde
    /// nummer. <c>null</c> als de dag vol zit (de teller blijft dan op het maximum staan).
    /// </summary>
    internal static async Task<int?> ReserveerVolgnummerAsync(string clubCode, DateOnly datum, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            MERGE [dbo].[WedstrijdnummerTeller] WITH (HOLDLOCK) AS t
            USING (SELECT {ClubScope.ClubCodeParam} AS [ClubCode], @Datum AS [Datum]) AS s
               ON t.[ClubCode] = s.[ClubCode] AND t.[Datum] = s.[Datum]
            WHEN MATCHED AND t.[LaatsteVolgnummer] < @Max THEN
                UPDATE SET [LaatsteVolgnummer] = t.[LaatsteVolgnummer] + 1
            WHEN NOT MATCHED THEN
                INSERT ([ClubCode], [Datum], [LaatsteVolgnummer]) VALUES (s.[ClubCode], s.[Datum], 1)
            OUTPUT inserted.[LaatsteVolgnummer];", conn);
        ClubScope.AddClubParam(cmd, clubCode);
        cmd.Parameters.Add("@Datum", System.Data.SqlDbType.Date).Value = datum.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.AddWithValue("@Max", ClubMatchWedstrijdNummer.MaxVolgnummer);
        return await cmd.ExecuteScalarAsync() as int?;
    }
}
