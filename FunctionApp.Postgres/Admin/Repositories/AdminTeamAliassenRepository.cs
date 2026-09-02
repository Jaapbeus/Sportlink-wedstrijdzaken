using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/AdminTeamAliassenRepository.cs</c>
/// (#887). Vertaling: <c>[dbo].[TeamAliassen]</c>/<c>[dbo].[Teams]</c> →
/// <c>public.teamaliassen</c>/<c>public.teams</c>, <c>TOP (@Limit)</c> → <c>LIMIT @limit</c>.
/// <b>Geen <c>DateTime.SpecifyKind</c> nodig</b> (in tegenstelling tot de SQL Server-tier): de
/// <c>mta_inserted</c>/<c>mta_modified</c>-kolommen zijn <c>TIMESTAMPTZ</c> en Npgsql geeft die al
/// terug met <c>Kind=Utc</c> (zie toelichting in <c>PostgresSchemaGenerator.GenerateHisTable</c>).
/// </summary>
internal static class AdminTeamAliassenRepository
{
    internal static readonly string[] GeldigeStatussen = ["pending", "validated", "rejected"];

    internal static async Task<(int count, int limit, List<Dictionary<string, object?>> items)> GetAsync(
        string clubCode, string? statusFilter, int limit, string cs)
    {
        var heeftStatusFilter = !string.IsNullOrWhiteSpace(statusFilter);
        var sql = $@"SELECT
                    ta.id, ta.ruwetekst, ta.ruwetekstgenormaliseerd,
                    ta.teamid, t.teamnaam, t.leeftijdscategorie,
                    ta.bron, ta.status, ta.aantalkeergebruikt,
                    ta.mta_inserted, ta.mta_modified
                FROM public.teamaliassen ta
                LEFT JOIN public.teams t
                    ON t.teamid = ta.teamid AND t.clubcode = ta.clubcode
                WHERE ta.clubcode = @cc
                  {(heeftStatusFilter ? "AND ta.status = @status" : "")}
                ORDER BY CASE WHEN ta.status = 'pending' THEN 0 ELSE 1 END,
                         ta.mta_inserted DESC
                LIMIT @limit";

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("cc", clubCode);
        if (heeftStatusFilter)
            cmd.Parameters.AddWithValue("status", statusFilter!);

        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(MapRow(r));
        return (list.Count, limit, list);
    }

    internal static async Task<(int pending, int validated, int rejected)> GetStatsAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT
                SUM(CASE WHEN status = 'pending'   THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'validated' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'rejected'  THEN 1 ELSE 0 END)
            FROM public.teamaliassen
            WHERE clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (0, 0, 0);
        return (r.IsDBNull(0) ? 0 : (int)r.GetInt64(0),
                r.IsDBNull(1) ? 0 : (int)r.GetInt64(1),
                r.IsDBNull(2) ? 0 : (int)r.GetInt64(2));
    }

    internal static async Task<int> ZetStatusAsync(int id, string status, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.teamaliassen
            SET status = @status, mta_modified = NOW()
            WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.teamaliassen WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static Dictionary<string, object?> MapRow(NpgsqlDataReader r) => new()
    {
        ["id"] = r.GetInt32(r.GetOrdinal("id")),
        ["ruweTekst"] = r.GetString(r.GetOrdinal("ruwetekst")),
        ["ruweTekstGenormaliseerd"] = r.GetString(r.GetOrdinal("ruwetekstgenormaliseerd")),
        ["teamId"] = r.GetInt32(r.GetOrdinal("teamid")),
        ["teamnaam"] = Nullable(r, "teamnaam"),
        ["leeftijdsCategorie"] = Nullable(r, "leeftijdscategorie"),
        ["bron"] = r.GetString(r.GetOrdinal("bron")),
        ["status"] = r.GetString(r.GetOrdinal("status")),
        ["aantalKeerGebruikt"] = r.GetInt32(r.GetOrdinal("aantalkeergebruikt")),
        ["mtaInserted"] = NullableDateTime(r, "mta_inserted"),
        ["mtaModified"] = NullableDateTime(r, "mta_modified"),
    };

    private static string? Nullable(NpgsqlDataReader r, string kolom)
    {
        var i = r.GetOrdinal(kolom);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static DateTime? NullableDateTime(NpgsqlDataReader r, string kolom)
    {
        var i = r.GetOrdinal(kolom);
        return r.IsDBNull(i) ? null : r.GetDateTime(i);
    }
}
