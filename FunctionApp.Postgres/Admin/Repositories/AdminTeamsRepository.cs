using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/AdminTeamsRepository.cs</c>
/// (#887). Vertaling: <c>[dbo].[Teams]</c> → <c>public.teams</c>.
/// </summary>
internal static class AdminTeamsRepository
{
    internal static async Task<List<string>> GetTeamnamenAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT teamnaam
            FROM public.teams
            WHERE clubcode = @cc AND isactief = TRUE
            ORDER BY teamnaam", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await r.ReadAsync())
            list.Add(r.GetString(0));
        return list;
    }

    internal static async Task<List<object>> GetKeuzelijstAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT teamid, teamnaam, leeftijdscategorie
            FROM public.teams
            WHERE clubcode = @cc AND isactief = TRUE
            ORDER BY teamnaam", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await r.ReadAsync())
            list.Add(new { teamId = r.GetInt32(0), teamnaam = r.GetString(1), leeftijdsCategorie = r.IsDBNull(2) ? null : r.GetString(2) });
        return list;
    }
}
