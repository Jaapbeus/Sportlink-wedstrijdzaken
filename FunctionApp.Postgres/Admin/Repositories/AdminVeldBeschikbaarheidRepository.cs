using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Admin/Repositories/AdminVeldBeschikbaarheidRepository.cs</c> (#887). Vertaling:
/// <c>[dbo].[VeldBeschikbaarheid]</c>/<c>[dbo].[Velden]</c>/<c>[dbo].[VeldPeriode]</c> →
/// <c>public.veldbeschikbaarheid</c>/<c>public.velden</c>/<c>public.veldperiode</c>,
/// <c>CONVERT(VARCHAR(5), …)</c> → <c>to_char(…, 'HH24:MI')</c>, <c>OUTPUT INSERTED.[Id]</c> →
/// <c>RETURNING id</c>. Zie #855-toelichting in <c>AdminVeldPeriodeRepository</c> voor de
/// gequote-alias-regel bij elke reader-gevoede responsdictionary.
/// </summary>
internal static class AdminVeldBeschikbaarheidRepository
{
    internal static async Task<List<Dictionary<string, object?>>> GetAlleAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT vb.id AS ""Id"", vb.veldnummer AS ""VeldNummer"", v.veldnaam AS ""VeldNaam"",
                   vb.dagvanweek AS ""DagVanWeek"",
                   to_char(vb.beschikbaarvanaf, 'HH24:MI') AS ""BeschikbaarVanaf"",
                   to_char(vb.beschikbaartot, 'HH24:MI')   AS ""BeschikbaarTot"",
                   vb.gebruikzonsondergang AS ""GebruikZonsondergang"", vb.periodeid AS ""PeriodeId"",
                   p.naam AS ""PeriodeNaam""
            FROM public.veldbeschikbaarheid vb
            JOIN public.velden v ON v.veldnummer = vb.veldnummer
            LEFT JOIN public.veldperiode p ON p.id = vb.periodeid
            WHERE vb.clubcode = @cc
            ORDER BY vb.dagvanweek, vb.veldnummer", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(RepositoryRijMapper.LeesRij(r));
        return list;
    }

    internal static async Task<List<Dictionary<string, object?>>> GetVeldenAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT veldnummer, veldnaam, veldtype, heeftkunstlicht, actief
              FROM public.velden WHERE clubcode = @cc ORDER BY veldnummer", conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
            list.Add(new()
            {
                ["VeldNummer"] = r.GetInt32(0),
                ["VeldNaam"] = r.GetString(1),
                ["VeldType"] = r.GetString(2),
                ["HeeftKunstlicht"] = r.GetBoolean(3),
                ["Actief"] = r.GetBoolean(4)
            });
        return list;
    }

    internal static async Task<bool> VeldNummerBestaatAsync(int veldNummer, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(1) FROM public.velden WHERE veldnummer = @vn", conn);
        cmd.Parameters.AddWithValue("vn", veldNummer);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    internal static async Task InsertVeldAsync(
        int veldNummer, string veldNaam, string veldType, bool heeftKunstlicht, bool actief,
        string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.velden (veldnummer, veldnaam, veldtype, heeftkunstlicht, actief, clubcode)
            VALUES (@vn, @naam, @type, @licht, @act, @cc)", conn);
        cmd.Parameters.AddWithValue("vn", veldNummer);
        cmd.Parameters.AddWithValue("naam", veldNaam);
        cmd.Parameters.AddWithValue("type", veldType);
        cmd.Parameters.AddWithValue("licht", heeftKunstlicht);
        cmd.Parameters.AddWithValue("act", actief);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> UpdateVeldAsync(
        int veldNummer, string veldNaam, string veldType, bool heeftKunstlicht, bool actief,
        string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.velden
            SET veldnaam = @naam, veldtype = @type, heeftkunstlicht = @licht, actief = @act
            WHERE veldnummer = @vn AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("vn", veldNummer);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("naam", veldNaam);
        cmd.Parameters.AddWithValue("type", veldType);
        cmd.Parameters.AddWithValue("licht", heeftKunstlicht);
        cmd.Parameters.AddWithValue("act", actief);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> UpdateAsync(
        int id, TimeSpan vanf, TimeSpan tot, bool zon, int? periodeId, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.veldbeschikbaarheid
            SET beschikbaarvanaf = @vanf, beschikbaartot = @tot, gebruikzonsondergang = @zon,
                periodeid = @periodeid
            WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("vanf", vanf);
        cmd.Parameters.AddWithValue("tot", tot);
        cmd.Parameters.AddWithValue("zon", zon);
        cmd.Parameters.AddWithValue("periodeid", (object?)periodeId ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<bool> BestaatAsync(int veldNummer, int dagVanWeek, int? periodeId, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(1) FROM public.veldbeschikbaarheid
            WHERE veldnummer = @vn AND dagvanweek = @dag AND clubcode = @cc
              AND ((periodeid IS NULL AND @periodeid::int IS NULL) OR periodeid = @periodeid)", conn);
        cmd.Parameters.AddWithValue("vn", veldNummer);
        cmd.Parameters.AddWithValue("dag", dagVanWeek);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("periodeid", (object?)periodeId ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    internal static async Task<int> InsertAsync(
        int veldNummer, int dagVanWeek, TimeSpan vanf, TimeSpan tot, bool zon, int? periodeId, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.veldbeschikbaarheid
                (veldnummer, dagvanweek, beschikbaarvanaf, beschikbaartot, gebruikzonsondergang, periodeid, clubcode)
            VALUES (@vn, @dag, @vanf, @tot, @zon, @periodeid, @cc)
            RETURNING id", conn);
        cmd.Parameters.AddWithValue("vn", veldNummer);
        cmd.Parameters.AddWithValue("dag", dagVanWeek);
        cmd.Parameters.AddWithValue("vanf", vanf);
        cmd.Parameters.AddWithValue("tot", tot);
        cmd.Parameters.AddWithValue("zon", zon);
        cmd.Parameters.AddWithValue("periodeid", (object?)periodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> DeleteAsync(int id, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.veldbeschikbaarheid WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
