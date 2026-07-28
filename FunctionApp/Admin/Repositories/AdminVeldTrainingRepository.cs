using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

/// <summary>
/// Repository voor dbo.VeldTraining — terugkerende trainingsbezetting per veld per weekdag.
/// Vrij per club instelbaar (#679, spoor B): geen vaste regimes, elke club legt zelf vast welke
/// velden op welke dag door training bezet zijn.
/// </summary>
internal static class AdminVeldTrainingRepository
{
    internal static async Task<List<Dictionary<string, object?>>> GetAlleAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT t.[Id], t.[VeldNummer], v.[VeldNaam], t.[DagVanWeek],
                   CONVERT(VARCHAR(5), t.[VanTijd]) AS [VanTijd],
                   CONVERT(VARCHAR(5), t.[TotTijd]) AS [TotTijd],
                   t.[Omschrijving], t.[Actief]
            FROM [dbo].[VeldTraining] t
            JOIN [dbo].[Velden] v ON v.[VeldNummer] = t.[VeldNummer]
            WHERE t.[ClubCode] = @Cc
            ORDER BY t.[DagVanWeek], t.[VeldNummer], t.[VanTijd]", conn);
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

    internal static async Task<int> InsertAsync(
        int veldNummer, int dagVanWeek, TimeSpan vanTijd, TimeSpan totTijd, string? omschrijving,
        bool actief, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[VeldTraining]
                ([VeldNummer], [DagVanWeek], [VanTijd], [TotTijd], [Omschrijving], [Actief], [ClubCode])
            OUTPUT INSERTED.[Id]
            VALUES (@Vn, @Dag, @Van, @Tot, @Omschr, @Act, @Cc)", conn);
        cmd.Parameters.AddWithValue("@Vn",     veldNummer);
        cmd.Parameters.AddWithValue("@Dag",    dagVanWeek);
        cmd.Parameters.AddWithValue("@Van",    vanTijd);
        cmd.Parameters.AddWithValue("@Tot",    totTijd);
        cmd.Parameters.AddWithValue("@Omschr", (object?)omschrijving ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Act",    actief);
        cmd.Parameters.AddWithValue("@Cc",     clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> UpdateAsync(
        int id, int veldNummer, int dagVanWeek, TimeSpan vanTijd, TimeSpan totTijd, string? omschrijving,
        bool actief, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [dbo].[VeldTraining]
            SET [VeldNummer] = @Vn, [DagVanWeek] = @Dag, [VanTijd] = @Van, [TotTijd] = @Tot,
                [Omschrijving] = @Omschr, [Actief] = @Act
            WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id",     id);
        cmd.Parameters.AddWithValue("@Cc",     clubCode);
        cmd.Parameters.AddWithValue("@Vn",     veldNummer);
        cmd.Parameters.AddWithValue("@Dag",    dagVanWeek);
        cmd.Parameters.AddWithValue("@Van",    vanTijd);
        cmd.Parameters.AddWithValue("@Tot",    totTijd);
        cmd.Parameters.AddWithValue("@Omschr", (object?)omschrijving ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Act",    actief);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "DELETE FROM [dbo].[VeldTraining] WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
