using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/AdminVeldPeriodeRepository.cs</c>
/// (#887). Vertaling: <c>[dbo].[VeldPeriode]</c> → <c>public.veldperiode</c>,
/// <c>[dbo].[VeldBeschikbaarheid]</c> → <c>public.veldbeschikbaarheid</c>,
/// <c>CONVERT(VARCHAR(10), …, 23)</c> → <c>to_char(…, 'YYYY-MM-DD')</c>,
/// <c>OUTPUT INSERTED.[Id]</c> → <c>RETURNING id</c>.
/// <para>
/// <b>#855-casing:</b> Postgres vouwt ongequote kolomnamen naar lowercase, dus elke kolom die via
/// <c>r.GetName(i)</c> in de responsdictionary terechtkomt wordt hier expliciet met een gequote
/// PascalCase-alias geselecteerd (<c>id AS "Id"</c>) zodat de JSON-response exact hetzelfde veldnamen
/// oplevert als de SQL Server-tier — zonder deze aliassen zou de Blazor GUI met lowercase sleutels
/// (<c>id</c>, <c>naam</c>, …) een lege pagina renderen.
/// </para>
/// </summary>
internal static class AdminVeldPeriodeRepository
{
    internal static async Task<List<Dictionary<string, object?>>> GetAlleAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT id AS ""Id"", naam AS ""Naam"",
                   to_char(datumvan, 'YYYY-MM-DD') AS ""DatumVan"",
                   to_char(datumtot, 'YYYY-MM-DD') AS ""DatumTot"",
                   actief AS ""Actief""
            FROM public.veldperiode
            WHERE clubcode = @cc
            ORDER BY datumvan", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(RepositoryRijMapper.LeesRij(r));
        return list;
    }

    internal static async Task<bool> BestaatAsync(int id, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(1) FROM public.veldperiode WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    internal static async Task<bool> OverlaptMetAndereAsync(
        DateOnly datumVan, DateOnly datumTot, string clubCode, string cs, int? uitgesloten = null)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(1) FROM public.veldperiode
            WHERE clubcode = @cc AND actief = TRUE
              AND (@uitgesloten::int IS NULL OR id <> @uitgesloten)
              AND datumvan <= @tot AND datumtot >= @van", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("uitgesloten", (object?)uitgesloten ?? DBNull.Value);
        cmd.Parameters.AddWithValue("van", datumVan.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("tot", datumTot.ToDateTime(TimeOnly.MinValue));
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    internal static async Task<int> InsertAsync(
        string naam, DateOnly datumVan, DateOnly datumTot, bool actief, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.veldperiode (naam, datumvan, datumtot, actief, clubcode)
            VALUES (@naam, @van, @tot, @act, @cc)
            RETURNING id", conn);
        cmd.Parameters.AddWithValue("naam", naam);
        cmd.Parameters.AddWithValue("van", datumVan.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("tot", datumTot.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("act", actief);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> UpdateAsync(
        int id, string naam, DateOnly datumVan, DateOnly datumTot, bool actief, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.veldperiode
            SET naam = @naam, datumvan = @van, datumtot = @tot, actief = @act
            WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("naam", naam);
        cmd.Parameters.AddWithValue("van", datumVan.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("tot", datumTot.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("act", actief);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<bool> InGebruikAsync(int id, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(1) FROM public.veldbeschikbaarheid WHERE periodeid = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.veldperiode WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
