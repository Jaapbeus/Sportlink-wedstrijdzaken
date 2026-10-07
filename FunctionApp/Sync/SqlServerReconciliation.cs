using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction;

/// <summary>
/// Reconciliatie na de merge (#1558, pariteit met #1193 op de Postgres-tier): his-rijen die niet meer
/// in de zojuist geladen stg-snapshot voorkomen worden zacht verwijderd (<c>mta_deleted</c>), nooit
/// hard — <c>his</c> is een audit-trail. Regels gelijk aan
/// <c>PostgresMergeOrchestrator.ReconcileWindowedAsync</c>/<c>ReconcileFullScopeAsync</c>:
/// <list type="bullet">
/// <item>alleen de gesyncte club (<c>ClubCode = @clubCode</c>, dus nooit AllStars of NULL-rijen);</item>
/// <item>matches: venster = MIN/MAX van <c>kaledatum</c> in stg voor deze club, en nooit vóór de
/// gedeelde ondergrens (<c>Planner.Shared.Sync.ReconciliatieOndergrens</c>); geen stg-rijen = niets doen;</item>
/// <item>teams: volledige snapshot per run, dus volledige clubscope;</item>
/// <item>de aanroeper roept dit alleen aan als de bijbehorende fetch-fase slaagde.</item>
/// </list>
/// Het herstel van een terugkerende rij zit in <c>sp_MergeStgToHis</c>.
/// </summary>
internal static class SqlServerReconciliation
{
    private const string MatchesSql = @"
        UPDATE h
        SET h.[mta_deleted] = GETUTCDATE()
        FROM [his].[matches] h
        WHERE h.[mta_deleted] IS NULL
          AND h.[ClubCode] = @clubCode
          AND CAST(h.[kaledatum] AS DATE) BETWEEN @van AND @tot
          AND NOT EXISTS (
              SELECT 1 FROM [stg].[matches] s
              WHERE s.[ClubCode] = @clubCode
                AND (s.[wedstrijdcode] = h.[wedstrijdcode]
                     OR (s.[wedstrijdcode] IS NULL AND h.[wedstrijdcode] IS NULL)));";

    private const string TeamsSql = @"
        UPDATE h
        SET h.[mta_deleted] = GETUTCDATE()
        FROM [his].[teams] h
        WHERE h.[mta_deleted] IS NULL
          AND h.[ClubCode] = @clubCode
          AND NOT EXISTS (
              SELECT 1 FROM [stg].[teams] s
              WHERE s.[ClubCode] = @clubCode
                AND (s.[teamcode] = h.[teamcode] OR (s.[teamcode] IS NULL AND h.[teamcode] IS NULL))
                AND (s.[lokaleteamcode] = h.[lokaleteamcode] OR (s.[lokaleteamcode] IS NULL AND h.[lokaleteamcode] IS NULL))
                AND (s.[poulecode] = h.[poulecode] OR (s.[poulecode] IS NULL AND h.[poulecode] IS NULL)));";

    /// <summary>Best-effort per entiteit: his.* is al gemerged, een fout hier laat de sync niet falen.</summary>
    internal static async Task ReconcileVerdwenenAsync(
        string clubCode, bool teamsFailed, bool matchesFailed, DateOnly ondergrens, ILogger log)
    {
        if (!teamsFailed)
        {
            try
            {
                var aantal = await ReconcileTeamsAsync(clubCode);
                if (aantal > 0)
                    log.LogInformation("RECONCILIATIE - {Aantal} his.teams-rij(en) gemarkeerd als verwijderd voor {ClubCode}", aantal, clubCode);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "RECONCILIATIE - his.teams mislukt voor {ClubCode}", clubCode);
            }
        }

        if (!matchesFailed)
        {
            try
            {
                var aantal = await ReconcileMatchesAsync(clubCode, ondergrens);
                if (aantal > 0)
                    log.LogInformation("RECONCILIATIE - {Aantal} his.matches-rij(en) gemarkeerd als verwijderd voor {ClubCode}", aantal, clubCode);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "RECONCILIATIE - his.matches mislukt voor {ClubCode}", clubCode);
            }
        }
    }

    internal static async Task<int> ReconcileTeamsAsync(string clubCode)
    {
        await using var conn = new SqlConnection(DatabaseConfig.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(TeamsSql, conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> ReconcileMatchesAsync(string clubCode, DateOnly ondergrens)
    {
        await using var conn = new SqlConnection(DatabaseConfig.ConnectionString);
        await conn.OpenAsync();

        DateOnly van, tot;
        await using (var bounds = new SqlCommand(
            "SELECT MIN(CAST([kaledatum] AS DATE)), MAX(CAST([kaledatum] AS DATE)) FROM [stg].[matches] WHERE [ClubCode] = @clubCode;", conn))
        {
            bounds.Parameters.AddWithValue("@clubCode", clubCode);
            await using var reader = await bounds.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.IsDBNull(0) || reader.IsDBNull(1))
                return 0; // Geen stg-rijen voor deze club: niets om tegen te reconciliëren.
            van = DateOnly.FromDateTime(reader.GetDateTime(0));
            tot = DateOnly.FromDateTime(reader.GetDateTime(1));
        }

        if (ondergrens > van) van = ondergrens;
        if (van > tot) return 0; // Het hele stg-venster ligt vóór de ondergrens.

        await using var cmd = new SqlCommand(MatchesSql, conn);
        cmd.Parameters.AddWithValue("@clubCode", clubCode);
        cmd.Parameters.Add("@van", System.Data.SqlDbType.Date).Value = van.ToDateTime(TimeOnly.MinValue);
        cmd.Parameters.Add("@tot", System.Data.SqlDbType.Date).Value = tot.ToDateTime(TimeOnly.MinValue);
        return await cmd.ExecuteNonQueryAsync();
    }
}
