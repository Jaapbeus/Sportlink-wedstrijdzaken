using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal record SpeeltijdInput(
    string Leeftijd, decimal Veldafmeting,
    int WedstrijdTotaal, int WedstrijdHelft, int WedstrijdRust);

internal static class AdminSpeeltijdenRepository
{
    internal static async Task<List<object>> GetAlleAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust]
            FROM [dbo].[Speeltijden]
            WHERE [ClubCode] = @ClubCode
            ORDER BY
                CASE WHEN [Leeftijd] LIKE 'JO%' THEN TRY_CAST(SUBSTRING([Leeftijd], 3, 10) AS INT)
                     WHEN [Leeftijd] LIKE 'MO%' THEN 1000 + TRY_CAST(SUBSTRING([Leeftijd], 3, 10) AS INT)
                     WHEN [Leeftijd] LIKE 'G%'  THEN 2000
                     WHEN [Leeftijd] = 'VR'     THEN 3000
                     ELSE 4000 + TRY_CAST([Leeftijd] AS INT)
                END", conn);
        cmd.Parameters.AddWithValue("@ClubCode", clubCode);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await r.ReadAsync())
            list.Add(new
            {
                Leeftijd = r.GetString(0), Veldafmeting = r.GetDecimal(1),
                WedstrijdTotaal = r.GetInt32(2), WedstrijdHelft = r.GetInt32(3), WedstrijdRust = r.GetInt32(4)
            });
        return list;
    }

    internal static async Task InsertAsync(SpeeltijdInput i, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[Speeltijden]
                ([Leeftijd], [Veldafmeting], [WedstrijdTotaal], [WedstrijdHelft], [WedstrijdRust], [ClubCode])
            VALUES (@L, @Vf, @Wt, @Wh, @Wr, @Cc)", conn);
        cmd.Parameters.AddWithValue("@L",  i.Leeftijd.Trim());
        cmd.Parameters.AddWithValue("@Vf", i.Veldafmeting);
        cmd.Parameters.AddWithValue("@Wt", i.WedstrijdTotaal);
        cmd.Parameters.AddWithValue("@Wh", i.WedstrijdHelft);
        cmd.Parameters.AddWithValue("@Wr", i.WedstrijdRust);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> UpdateAsync(string leeftijd, SpeeltijdInput i, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [dbo].[Speeltijden]
            SET [Veldafmeting] = @Vf, [WedstrijdTotaal] = @Wt, [WedstrijdHelft] = @Wh, [WedstrijdRust] = @Wr
            WHERE [Leeftijd] = @L AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@L",  leeftijd);
        cmd.Parameters.AddWithValue("@Vf", i.Veldafmeting);
        cmd.Parameters.AddWithValue("@Wt", i.WedstrijdTotaal);
        cmd.Parameters.AddWithValue("@Wh", i.WedstrijdHelft);
        cmd.Parameters.AddWithValue("@Wr", i.WedstrijdRust);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> DeleteAsync(string leeftijd, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "DELETE FROM [dbo].[Speeltijden] WHERE [Leeftijd] = @L AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@L",  leeftijd);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
