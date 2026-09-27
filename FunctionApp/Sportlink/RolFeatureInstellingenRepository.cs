using Microsoft.Data.SqlClient;
using Planner.Shared.Autorisatie;
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

    /// <summary>
    /// De volledige toegangsmatrix voor deze club (#1390): elke combinatie van
    /// <see cref="RolNamen.Alle"/> × (<see cref="SportlinkRolFeature.Alle"/> ∪ <see cref="MenuFeatureKeys.Alle"/>),
    /// fail-closed (ontbrekende rij = <c>false</c>). Los van <see cref="GetAllAsync"/>, dat uitsluitend
    /// de 3 Sportlink-FeatureKeys voor één rol teruggeeft en door <c>SportlinkMatchFunction</c> wordt
    /// gebruikt — dat pad blijft ongewijzigd. Filtert op instelbare rollen in C# (geen array-parameter
    /// nodig) omdat <c>Microsoft.Data.SqlClient</c> geen native array-binding kent.
    /// </summary>
    internal static async Task<Dictionary<(string RolNaam, string FeatureKey), bool>> GetMatrixAsync(string clubCode, string cs)
    {
        var instelbareRollen = new HashSet<string>(RolNamen.Alle, StringComparer.Ordinal);
        var resultaat = new Dictionary<(string, string), bool>();
        foreach (var rol in RolNamen.Alle)
            foreach (var featureKey in RolFeatureMatrixCore.AlleFeatureKeys)
                resultaat[(rol, featureKey)] = false;

        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand($@"
            SELECT [RolNaam], [FeatureKey], [Enabled] FROM [dbo].[RolFeatureInstellingen]
            WHERE [ClubCode] = {ClubScope.ClubCodeParam}", conn);
        ClubScope.AddClubParam(cmd, clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var rolNaam = r.GetString(0);
            if (!instelbareRollen.Contains(rolNaam)) continue;
            var sleutel = (rolNaam, r.GetString(1));
            if (resultaat.ContainsKey(sleutel))
                resultaat[sleutel] = r.GetBoolean(2);
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
