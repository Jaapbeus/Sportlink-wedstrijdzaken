using Microsoft.Data.SqlClient;

namespace SportlinkFunction.Admin;

internal static class AdminVoorkeurTijdenRepository
{
    // ── VoorkeurTijden ──

    internal static async Task<List<Dictionary<string, object?>>> GetVoorkeurTijdenAsync(string clubCode, string? team, string cs)
    {
        var sql = @"SELECT [Id], [TeamNaam], [DagVanWeek], CONVERT(VARCHAR(5), [VoorkeurTijd]) AS [VoorkeurTijd],
                           [Prioriteit], [Actief], [ClubCode], [mta_inserted], [mta_modified]
                    FROM [dbo].[TeamVoorkeurTijden]
                    WHERE [ClubCode] = @Cc";
        if (team != null) sql += " AND [TeamNaam] = @Team";
        sql += " ORDER BY [TeamNaam], [DagVanWeek], [VoorkeurTijd]";

        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        if (team != null) cmd.Parameters.AddWithValue("@Team", team);
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

    internal static async Task<int> InsertVoorkeurTijdAsync(
        string teamNaam, int dagVanWeek, TimeSpan voorkeurTijd, int prioriteit, bool actief,
        string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[TeamVoorkeurTijden]
                ([TeamNaam], [DagVanWeek], [VoorkeurTijd], [Prioriteit], [Actief], [ClubCode])
            VALUES (@Tn, @Dag, @Tijd, @Pr, @Act, @Cc);
            SELECT CAST(SCOPE_IDENTITY() AS INT);", conn);
        cmd.Parameters.AddWithValue("@Tn",  teamNaam);
        cmd.Parameters.AddWithValue("@Dag", dagVanWeek);
        cmd.Parameters.AddWithValue("@Tijd", voorkeurTijd);
        cmd.Parameters.AddWithValue("@Pr",  prioriteit);
        cmd.Parameters.AddWithValue("@Act", actief);
        cmd.Parameters.AddWithValue("@Cc",  clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> UpdateVoorkeurTijdAsync(
        int id, string teamNaam, int dagVanWeek, TimeSpan voorkeurTijd, int prioriteit, bool actief,
        string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [dbo].[TeamVoorkeurTijden]
            SET [TeamNaam] = @Tn, [DagVanWeek] = @Dag, [VoorkeurTijd] = @Tijd,
                [Prioriteit] = @Pr, [Actief] = @Act, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id",  id);
        cmd.Parameters.AddWithValue("@Cc",  clubCode);
        cmd.Parameters.AddWithValue("@Tn",  teamNaam);
        cmd.Parameters.AddWithValue("@Dag", dagVanWeek);
        cmd.Parameters.AddWithValue("@Tijd", voorkeurTijd);
        cmd.Parameters.AddWithValue("@Pr",  prioriteit);
        cmd.Parameters.AddWithValue("@Act", actief);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> SoftDeleteVoorkeurTijdAsync(int id, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [dbo].[TeamVoorkeurTijden]
            SET [Actief] = 0, [mta_modified] = GETUTCDATE()
            WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    // ── TeamRegels ──

    internal static async Task<List<Dictionary<string, object?>>> GetTeamRegelsAsync(string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            SELECT [Id], [TeamNaam], [RegelType], [WaardeMinuten], [WaardeVeldNummer],
                   CONVERT(VARCHAR(5), [WaardeTijd]) AS [WaardeTijd],
                   [Prioriteit], [Actief], [Opmerking], [ClubCode]
            FROM [dbo].[TeamRegels]
            WHERE [ClubCode] = @Cc
            ORDER BY [TeamNaam], [Prioriteit]", conn);
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

    internal static async Task<int> InsertTeamRegelAsync(
        string teamNaam, string regelType, int? waardeMinuten, int? waardeVeldNummer,
        TimeSpan? waardeTijd, int prioriteit, bool actief, string? opmerking,
        string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[TeamRegels]
                ([TeamNaam], [RegelType], [WaardeMinuten], [WaardeVeldNummer], [WaardeTijd],
                 [Prioriteit], [Actief], [Opmerking], [ClubCode])
            VALUES (@Tn, @Rt, @Wm, @Wvn, @Wt, @Pr, @Act, @Opm, @Cc);
            SELECT CAST(SCOPE_IDENTITY() AS INT);", conn);
        cmd.Parameters.AddWithValue("@Tn",  teamNaam);
        cmd.Parameters.AddWithValue("@Rt",  regelType);
        cmd.Parameters.AddWithValue("@Wm",  (object?)waardeMinuten    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Wvn", (object?)waardeVeldNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Wt",  (object?)waardeTijd       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Pr",  prioriteit);
        cmd.Parameters.AddWithValue("@Act", actief);
        cmd.Parameters.AddWithValue("@Opm", (object?)opmerking        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Cc",  clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> UpdateTeamRegelAsync(
        int id, string teamNaam, string regelType, int? waardeMinuten, int? waardeVeldNummer,
        TimeSpan? waardeTijd, int prioriteit, bool actief, string? opmerking,
        string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(@"
            UPDATE [dbo].[TeamRegels]
            SET [TeamNaam] = @Tn, [RegelType] = @Rt,
                [WaardeMinuten] = @Wm, [WaardeVeldNummer] = @Wvn, [WaardeTijd] = @Wt,
                [Prioriteit] = @Pr, [Actief] = @Act, [Opmerking] = @Opm
            WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id",  id);
        cmd.Parameters.AddWithValue("@Cc",  clubCode);
        cmd.Parameters.AddWithValue("@Tn",  teamNaam);
        cmd.Parameters.AddWithValue("@Rt",  regelType);
        cmd.Parameters.AddWithValue("@Wm",  (object?)waardeMinuten    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Wvn", (object?)waardeVeldNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Wt",  (object?)waardeTijd       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Pr",  prioriteit);
        cmd.Parameters.AddWithValue("@Act", actief);
        cmd.Parameters.AddWithValue("@Opm", (object?)opmerking        ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> SoftDeleteTeamRegelAsync(int id, string clubCode, string cs)
    {
        using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "UPDATE [dbo].[TeamRegels] SET [Actief] = 0 WHERE [Id] = @Id AND [ClubCode] = @Cc", conn);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
