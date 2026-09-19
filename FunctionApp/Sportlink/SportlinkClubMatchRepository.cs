using Microsoft.Data.SqlClient;
using Planner.Shared.Integrations.SportlinkClub;
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

        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new ClubMatchTeamKoppeling(
            r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetInt64(2),
            r.GetInt32(3));
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
}
