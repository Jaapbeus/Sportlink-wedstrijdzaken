using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal static class AdminVeldBeschikbaarheidRepository
{
    internal static async Task<List<Dictionary<string, object?>>> GetAlleAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT vb.[Id], vb.[VeldNummer], v.[VeldNaam], vb.[DagVanWeek],
                   CONVERT(VARCHAR(5), vb.[BeschikbaarVanaf]) AS [BeschikbaarVanaf],
                   CONVERT(VARCHAR(5), vb.[BeschikbaarTot])   AS [BeschikbaarTot],
                   vb.[GebruikZonsondergang]
            FROM [dbo].[VeldBeschikbaarheid] vb
            JOIN [dbo].[Velden] v ON v.[VeldNummer] = vb.[VeldNummer]
            WHERE vb.[ClubCode] = @Cc
            ORDER BY vb.[DagVanWeek], vb.[VeldNummer]", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    internal static async Task<List<Dictionary<string, object?>>> GetVeldenAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT [VeldNummer], [VeldNaam] FROM [dbo].[Velden] WHERE [ClubCode] = @Cc ORDER BY [VeldNummer]", conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(new() { ["VeldNummer"] = r.GetInt32(0), ["VeldNaam"] = r.GetString(1) });
        return list;
    }

    internal static async Task<int> UpdateAsync(int id, TimeSpan vanf, TimeSpan tot, bool zon, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [dbo].[VeldBeschikbaarheid]
            SET [BeschikbaarVanaf] = @Vanf, [BeschikbaarTot] = @Tot, [GebruikZonsondergang] = @Zon
            WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id",   id);
        cmd.Parameters.AddWithValue("@Cc",   clubCode);
        cmd.Parameters.AddWithValue("@Vanf", vanf);
        cmd.Parameters.AddWithValue("@Tot",  tot);
        cmd.Parameters.AddWithValue("@Zon",  zon);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<bool> BestaatAsync(int veldNummer, int dagVanWeek, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT COUNT(1) FROM [dbo].[VeldBeschikbaarheid]
            WHERE [VeldNummer] = @Vn AND [DagVanWeek] = @Dag AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Vn",  veldNummer);
        cmd.Parameters.AddWithValue("@Dag", dagVanWeek);
        cmd.Parameters.AddWithValue("@Cc",  clubCode);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    internal static async Task<int> InsertAsync(int veldNummer, int dagVanWeek, TimeSpan vanf, TimeSpan tot, bool zon, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[VeldBeschikbaarheid]
                ([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang], [ClubCode])
            OUTPUT INSERTED.[Id]
            VALUES (@Vn, @Dag, @Vanf, @Tot, @Zon, @Cc)", conn);
        cmd.Parameters.AddWithValue("@Vn",  veldNummer);
        cmd.Parameters.AddWithValue("@Dag", dagVanWeek);
        cmd.Parameters.AddWithValue("@Vanf", vanf);
        cmd.Parameters.AddWithValue("@Tot",  tot);
        cmd.Parameters.AddWithValue("@Zon",  zon);
        cmd.Parameters.AddWithValue("@Cc",   clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "DELETE FROM [dbo].[VeldBeschikbaarheid] WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
