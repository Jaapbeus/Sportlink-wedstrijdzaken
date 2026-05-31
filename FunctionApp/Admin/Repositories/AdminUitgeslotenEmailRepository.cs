using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal static class AdminUitgeslotenEmailRepository
{
    internal static async Task<List<Dictionary<string, object?>>> GetAlleAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [Id], [EmailAdres], [Omschrijving], [Actief], [ClubCode], [mta_inserted]
            FROM [dbo].[UitgeslotenEmailAdressen]
            WHERE [ClubCode] = @Cc
            ORDER BY [EmailAdres]", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(new()
            {
                ["id"]           = r.GetInt32(r.GetOrdinal("Id")),
                ["emailAdres"]   = r.GetString(r.GetOrdinal("EmailAdres")),
                ["omschrijving"] = r.IsDBNull(r.GetOrdinal("Omschrijving")) ? null : r.GetString(r.GetOrdinal("Omschrijving")),
                ["actief"]       = r.GetBoolean(r.GetOrdinal("Actief")),
                ["clubCode"]     = r.GetString(r.GetOrdinal("ClubCode")),
            });
        return list;
    }

    internal static async Task<int> InsertAsync(string emailAdres, string? omschrijving, bool actief, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[UitgeslotenEmailAdressen] ([EmailAdres], [Omschrijving], [Actief], [ClubCode])
            VALUES (@Email, @Omschr, @Actief, @Cc);
            SELECT CAST(SCOPE_IDENTITY() AS INT);", conn);
        cmd.Parameters.AddWithValue("@Email",  emailAdres);
        cmd.Parameters.AddWithValue("@Omschr", (object?)omschrijving ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Actief", actief);
        cmd.Parameters.AddWithValue("@Cc",     clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "DELETE FROM [dbo].[UitgeslotenEmailAdressen] WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
