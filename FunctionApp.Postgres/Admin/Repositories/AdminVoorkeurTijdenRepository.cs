using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp/Admin/Repositories/AdminVoorkeurTijdenRepository.cs</c> (#887). Vertaling:
/// <c>[dbo].[TeamVoorkeurTijden]</c>/<c>[dbo].[TeamRegels]</c> →
/// <c>public.teamvoorkeurtijden</c>/<c>public.teamregels</c>, <c>SCOPE_IDENTITY()</c> →
/// <c>RETURNING id</c>, gequote PascalCase-aliassen voor #855. <c>mta_inserted</c>/<c>mta_modified</c>
/// zijn in beide tiers al lowercase (bestaande uitzondering op de PascalCase-conventie) — geen
/// alias nodig; <c>mta_modified</c> gebruikt <c>NOW()</c> in een <c>TIMESTAMPTZ</c>-kolom (#851's B2).
/// </summary>
internal static class AdminVoorkeurTijdenRepository
{
    // ── VoorkeurTijden ──

    internal static async Task<List<Dictionary<string, object?>>> GetVoorkeurTijdenAsync(string clubCode, string? team, string cs)
    {
        var sql = @"SELECT id AS ""Id"", teamnaam AS ""TeamNaam"", dagvanweek AS ""DagVanWeek"",
                           to_char(voorkeurtijd, 'HH24:MI') AS ""VoorkeurTijd"",
                           prioriteit AS ""Prioriteit"", actief AS ""Actief"", clubcode AS ""ClubCode"",
                           mta_inserted, mta_modified
                    FROM public.teamvoorkeurtijden
                    WHERE clubcode = @cc";
        if (team != null) sql += " AND teamnaam = @team";
        sql += " ORDER BY teamnaam, dagvanweek, voorkeurtijd";

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("cc", clubCode);
        if (team != null) cmd.Parameters.AddWithValue("team", team);
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

    internal static async Task<int> InsertVoorkeurTijdAsync(
        string teamNaam, int dagVanWeek, TimeSpan voorkeurTijd, int prioriteit, bool actief,
        string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.teamvoorkeurtijden (teamnaam, dagvanweek, voorkeurtijd, prioriteit, actief, clubcode)
            VALUES (@tn, @dag, @tijd, @pr, @act, @cc)
            RETURNING id", conn);
        cmd.Parameters.AddWithValue("tn", teamNaam);
        cmd.Parameters.AddWithValue("dag", dagVanWeek);
        cmd.Parameters.AddWithValue("tijd", voorkeurTijd);
        cmd.Parameters.AddWithValue("pr", prioriteit);
        cmd.Parameters.AddWithValue("act", actief);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> UpdateVoorkeurTijdAsync(
        int id, string teamNaam, int dagVanWeek, TimeSpan voorkeurTijd, int prioriteit, bool actief,
        string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.teamvoorkeurtijden
            SET teamnaam = @tn, dagvanweek = @dag, voorkeurtijd = @tijd,
                prioriteit = @pr, actief = @act, mta_modified = NOW()
            WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("tn", teamNaam);
        cmd.Parameters.AddWithValue("dag", dagVanWeek);
        cmd.Parameters.AddWithValue("tijd", voorkeurTijd);
        cmd.Parameters.AddWithValue("pr", prioriteit);
        cmd.Parameters.AddWithValue("act", actief);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> SoftDeleteVoorkeurTijdAsync(int id, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.teamvoorkeurtijden
            SET actief = FALSE, mta_modified = NOW()
            WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    // ── TeamRegels ──

    internal static async Task<List<Dictionary<string, object?>>> GetTeamRegelsAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT id AS ""Id"", teamnaam AS ""TeamNaam"", regeltype AS ""RegelType"",
                   waardeminuten AS ""WaardeMinuten"", waardeveldnummer AS ""WaardeVeldNummer"",
                   to_char(waardetijd, 'HH24:MI') AS ""WaardeTijd"",
                   prioriteit AS ""Prioriteit"", actief AS ""Actief"", opmerking AS ""Opmerking"",
                   clubcode AS ""ClubCode""
            FROM public.teamregels
            WHERE clubcode = @cc
            ORDER BY teamnaam, prioriteit", conn);
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

    internal static async Task<int> InsertTeamRegelAsync(
        string teamNaam, string regelType, int? waardeMinuten, int? waardeVeldNummer,
        TimeSpan? waardeTijd, int prioriteit, bool actief, string? opmerking,
        string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.teamregels
                (teamnaam, regeltype, waardeminuten, waardeveldnummer, waardetijd,
                 prioriteit, actief, opmerking, clubcode)
            VALUES (@tn, @rt, @wm, @wvn, @wt, @pr, @act, @opm, @cc)
            RETURNING id", conn);
        cmd.Parameters.AddWithValue("tn", teamNaam);
        cmd.Parameters.AddWithValue("rt", regelType);
        cmd.Parameters.AddWithValue("wm", (object?)waardeMinuten ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wvn", (object?)waardeVeldNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wt", (object?)waardeTijd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pr", prioriteit);
        cmd.Parameters.AddWithValue("act", actief);
        cmd.Parameters.AddWithValue("opm", (object?)opmerking ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    internal static async Task<int> UpdateTeamRegelAsync(
        int id, string teamNaam, string regelType, int? waardeMinuten, int? waardeVeldNummer,
        TimeSpan? waardeTijd, int prioriteit, bool actief, string? opmerking,
        string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.teamregels
            SET teamnaam = @tn, regeltype = @rt,
                waardeminuten = @wm, waardeveldnummer = @wvn, waardetijd = @wt,
                prioriteit = @pr, actief = @act, opmerking = @opm
            WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        cmd.Parameters.AddWithValue("tn", teamNaam);
        cmd.Parameters.AddWithValue("rt", regelType);
        cmd.Parameters.AddWithValue("wm", (object?)waardeMinuten ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wvn", (object?)waardeVeldNummer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wt", (object?)waardeTijd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pr", prioriteit);
        cmd.Parameters.AddWithValue("act", actief);
        cmd.Parameters.AddWithValue("opm", (object?)opmerking ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> SoftDeleteTeamRegelAsync(int id, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE public.teamregels SET actief = FALSE WHERE id = @id AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
