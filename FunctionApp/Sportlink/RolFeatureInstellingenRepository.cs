using Microsoft.Data.SqlClient;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Planner;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Sportlink/RolFeatureInstellingenRepository.cs</c>
/// (#1341, epic #1338) — per-club, per-rol instelbare zichtbaarheid van Sportlink-acties.
/// </summary>
internal static class RolFeatureInstellingenRepository
{
    /// <summary>Fail-closed: geen rij voor deze combinatie betekent uitgeschakeld.</summary>
    internal static async Task<bool> IsEnabledAsync(string clubCode, string rolNaam, string featureKey, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT [Enabled] FROM [dbo].[RolFeatureInstellingen]
            WHERE [ClubCode] = {ClubScope.ClubCodeParam} AND [RolNaam] = @RolNaam AND [FeatureKey] = @FeatureKey", conn);
        ClubScope.AddClubParam(cmd, clubCode);
        cmd.Parameters.AddWithValue("@RolNaam", rolNaam);
        cmd.Parameters.AddWithValue("@FeatureKey", featureKey);
        var result = await cmd.ExecuteScalarAsync();
        return result is bool b && b;
    }

    /// <summary>Alle FeatureKeys uit <see cref="Planner.Shared.Integrations.SportlinkClub.SportlinkRolFeature.Alle"/>
    /// voor deze club/rol, met de huidige stand (ontbrekende rij = <c>false</c>).</summary>
    internal static async Task<Dictionary<string, bool>> GetAllAsync(string clubCode, string rolNaam, string cs)
    {
        var resultaat = SportlinkRolFeature.Alle.ToDictionary(fk => fk, _ => false);

        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT [FeatureKey], [Enabled] FROM [dbo].[RolFeatureInstellingen]
            WHERE [ClubCode] = {ClubScope.ClubCodeParam} AND [RolNaam] = @RolNaam", conn);
        ClubScope.AddClubParam(cmd, clubCode);
        cmd.Parameters.AddWithValue("@RolNaam", rolNaam);
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var featureKey = r.GetString(0);
            if (resultaat.ContainsKey(featureKey))
                resultaat[featureKey] = r.GetBoolean(1);
        }
        return resultaat;
    }

    internal static async Task SetAsync(string clubCode, string rolNaam, string featureKey, bool enabled, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            MERGE [dbo].[RolFeatureInstellingen] AS target
            USING (SELECT {ClubScope.ClubCodeParam} AS ClubCode, @RolNaam AS RolNaam, @FeatureKey AS FeatureKey) AS src
            ON target.[ClubCode] = src.ClubCode AND target.[RolNaam] = src.RolNaam AND target.[FeatureKey] = src.FeatureKey
            WHEN MATCHED THEN UPDATE SET [Enabled] = @Enabled
            WHEN NOT MATCHED THEN INSERT ([ClubCode], [RolNaam], [FeatureKey], [Enabled])
                VALUES ({ClubScope.ClubCodeParam}, @RolNaam, @FeatureKey, @Enabled);", conn);
        ClubScope.AddClubParam(cmd, clubCode);
        cmd.Parameters.AddWithValue("@RolNaam", rolNaam);
        cmd.Parameters.AddWithValue("@FeatureKey", featureKey);
        cmd.Parameters.AddWithValue("@Enabled", enabled);
        await cmd.ExecuteNonQueryAsync();
    }
}
