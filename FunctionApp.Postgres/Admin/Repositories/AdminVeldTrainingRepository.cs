using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/AdminVeldTrainingRepository.cs</c>
/// (#887). Vertaling: <c>[dbo].[VeldTraining]</c> → <c>public.veldtraining</c>,
/// <c>CONVERT(VARCHAR(5), …)</c> → <c>to_char(…, 'HH24:MI')</c>, <c>OUTPUT INSERTED.[Id]</c> →
/// <c>RETURNING id</c>, gequote PascalCase-aliassen voor de #855-casing-regel.
/// </summary>
internal static class AdminVeldTrainingRepository
{
    internal static async Task<List<Dictionary<string, object?>>> GetAlleAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT t.id AS ""Id"", t.veldnummer AS ""VeldNummer"", v.veldnaam AS ""VeldNaam"",
                   t.dagvanweek AS ""DagVanWeek"",
                   to_char(t.vantijd, 'HH24:MI') AS ""VanTijd"",
                   to_char(t.tottijd, 'HH24:MI') AS ""TotTijd"",
                   t.omschrijving AS ""Omschrijving"", t.actief AS ""Actief""
            FROM public.veldtraining t
            JOIN public.velden v ON v.veldnummer = t.veldnummer
            WHERE t.clubcode = @cc
            ORDER BY t.dagvanweek, t.veldnummer, t.vantijd", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
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
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.veldtraining
                (veldnummer, dagvanweek, vantijd, tottijd, omschrijving, actief, clubcode)
            VALUES (@vn, @dag, @van, @tot, @omschr, @act, @cc)
            RETURNING id", conn);
        cmd.Parameters.AddWithValue("vn", veldNummer);
        cmd.Parameters.AddWithValue("dag", dagVanWeek);
        cmd.Parameters.AddWithValue("van", vanTijd);
        cmd.Parameters.AddWithValue("tot", totTijd);
        cmd.Parameters.AddWithValue("omschr", (object?)omschrijving ?? DBNull.Value);
        cmd.Parameters.AddWithValue("act", actief);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> UpdateAsync(
        int id, int veldNummer, int dagVanWeek, TimeSpan vanTijd, TimeSpan totTijd, string? omschrijving,
        bool actief, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.veldtraining
            SET veldnummer = @vn, dagvanweek = @dag, vantijd = @van, tottijd = @tot,
                omschrijving = @omschr, actief = @act
            WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("vn", veldNummer);
        cmd.Parameters.AddWithValue("dag", dagVanWeek);
        cmd.Parameters.AddWithValue("van", vanTijd);
        cmd.Parameters.AddWithValue("tot", totTijd);
        cmd.Parameters.AddWithValue("omschr", (object?)omschrijving ?? DBNull.Value);
        cmd.Parameters.AddWithValue("act", actief);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.veldtraining WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
