using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Admin/Repositories/AdminUitgeslotenEmailRepository.cs</c> (#887). Vertaling:
/// <c>[dbo].[UitgeslotenEmailAdressen]</c> → <c>public.uitgeslotenemailadressen</c>,
/// <c>SCOPE_IDENTITY()</c> → <c>RETURNING id</c>. Gebruikt al expliciete camelCase-sleutels
/// (geen #855-aliasprobleem — de originele repository leunt hier al niet op reader-kolomcasing).
/// </summary>
internal static class AdminUitgeslotenEmailRepository
{
    internal static async Task<List<Dictionary<string, object?>>> GetAlleAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, emailadres, omschrijving, actief, clubcode, mta_inserted
            FROM public.uitgeslotenemailadressen
            WHERE clubcode = @cc
            ORDER BY emailadres", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(new()
            {
                ["id"] = r.GetInt32(r.GetOrdinal("id")),
                ["emailAdres"] = r.GetString(r.GetOrdinal("emailadres")),
                ["omschrijving"] = r.IsDBNull(r.GetOrdinal("omschrijving")) ? null : r.GetString(r.GetOrdinal("omschrijving")),
                ["actief"] = r.GetBoolean(r.GetOrdinal("actief")),
                ["clubCode"] = r.GetString(r.GetOrdinal("clubcode")),
            });
        return list;
    }

    internal static async Task<int> InsertAsync(string emailAdres, string? omschrijving, bool actief, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.uitgeslotenemailadressen (emailadres, omschrijving, actief, clubcode)
            VALUES (@email, @omschr, @actief, @cc)
            RETURNING id", conn);
        cmd.Parameters.AddWithValue("email", emailAdres);
        cmd.Parameters.AddWithValue("omschr", (object?)omschrijving ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actief", actief);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.uitgeslotenemailadressen WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
