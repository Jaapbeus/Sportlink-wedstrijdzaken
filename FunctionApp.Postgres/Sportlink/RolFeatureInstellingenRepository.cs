using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Leesqueries/CRUD voor <c>public.rolfeatureinstellingen</c> (#1341, epic #1338) — per-club,
/// per-rol instelbare zichtbaarheid van Sportlink-acties. SQL Server-tegenhanger:
/// <c>FunctionApp/Sportlink/RolFeatureInstellingenRepository.cs</c> (#1266-precedent: geen
/// gedeelde providerabstractie, docs/ARCHITECTUUR-DATABASE-TIERS.md).
/// </summary>
internal static class RolFeatureInstellingenRepository
{
    /// <summary>Fail-closed: geen rij voor deze combinatie betekent uitgeschakeld.</summary>
    internal static async Task<bool> IsEnabledAsync(string clubCode, string rolNaam, string featureKey, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT enabled FROM public.rolfeatureinstellingen
            WHERE clubcode = @cc AND rolnaam = @rol AND featurekey = @fk", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("rol", rolNaam);
        cmd.Parameters.AddWithValue("fk", featureKey);
        return await cmd.ExecuteScalarAsync() is true;
    }

    /// <summary>Alle FeatureKeys uit <see cref="Planner.Shared.Integrations.SportlinkClub.SportlinkRolFeature.Alle"/>
    /// voor deze club/rol, met de huidige stand (ontbrekende rij = <c>false</c>).</summary>
    internal static async Task<Dictionary<string, bool>> GetAllAsync(string clubCode, string rolNaam, string cs)
    {
        var resultaat = SportlinkRolFeature.Alle.ToDictionary(fk => fk, _ => false);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT featurekey, enabled FROM public.rolfeatureinstellingen
            WHERE clubcode = @cc AND rolnaam = @rol", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("rol", rolNaam);
        await using var r = await cmd.ExecuteReaderAsync();
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
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.rolfeatureinstellingen (clubcode, rolnaam, featurekey, enabled)
            VALUES (@cc, @rol, @fk, @en)
            ON CONFLICT (clubcode, rolnaam, featurekey) DO UPDATE SET enabled = @en", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("rol", rolNaam);
        cmd.Parameters.AddWithValue("fk", featureKey);
        cmd.Parameters.AddWithValue("en", enabled);
        await cmd.ExecuteNonQueryAsync();
    }
}
