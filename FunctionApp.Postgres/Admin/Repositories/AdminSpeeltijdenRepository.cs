using Npgsql;

namespace FunctionApp.Postgres.Admin;

internal record SpeeltijdInput(
    string Leeftijd, decimal Veldafmeting,
    int WedstrijdTotaal, int WedstrijdHelft, int WedstrijdRust,
    TimeOnly? StandaardVoorkeurTijd);

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/Repositories/AdminSpeeltijdenRepository.cs</c>
/// (#887). Vertaling: <c>[dbo].[Speeltijden]</c> → <c>public.speeltijden</c>, <c>TRY_CAST</c> (geen
/// Postgres-equivalent) → een regex-guard (<c>~ '^[0-9]+$'</c>) vóór de <c>::int</c>-cast, en
/// <c>CONVERT(VARCHAR(5), …)</c> → <c>to_char(…, 'HH24:MI')</c>.
/// </summary>
internal static class AdminSpeeltijdenRepository
{
    internal static async Task<List<object>> GetAlleAsync(string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust,
                   to_char(standaardvoorkeurtijd, 'HH24:MI') AS standaardvoorkeurtijd
            FROM public.speeltijden
            WHERE clubcode = @clubcode
            ORDER BY
                CASE WHEN leeftijd LIKE 'JO%' AND substring(leeftijd from 3) ~ '^[0-9]+$'
                          THEN substring(leeftijd from 3)::int
                     WHEN leeftijd LIKE 'MO%' AND substring(leeftijd from 3) ~ '^[0-9]+$'
                          THEN 1000 + substring(leeftijd from 3)::int
                     WHEN leeftijd LIKE 'G%' THEN 2000
                     WHEN leeftijd = 'VR' THEN 3000
                     WHEN leeftijd ~ '^[0-9]+$' THEN 4000 + leeftijd::int
                     ELSE 9000
                END", conn);
        cmd.Parameters.AddWithValue("clubcode", clubCode);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await r.ReadAsync())
            list.Add(new
            {
                Leeftijd = r.GetString(0),
                Veldafmeting = r.GetDecimal(1),
                WedstrijdTotaal = r.GetInt32(2),
                WedstrijdHelft = r.GetInt32(3),
                WedstrijdRust = r.GetInt32(4),
                StandaardVoorkeurTijd = r.IsDBNull(5) ? null : r.GetString(5)
            });
        return list;
    }

    internal static async Task InsertAsync(SpeeltijdInput i, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.speeltijden
                (leeftijd, veldafmeting, wedstrijdtotaal, wedstrijdhelft, wedstrijdrust,
                 standaardvoorkeurtijd, clubcode)
            VALUES (@l, @vf, @wt, @wh, @wr, @svt, @cc)", conn);
        cmd.Parameters.AddWithValue("l", i.Leeftijd.Trim());
        cmd.Parameters.AddWithValue("vf", i.Veldafmeting);
        cmd.Parameters.AddWithValue("wt", i.WedstrijdTotaal);
        cmd.Parameters.AddWithValue("wh", i.WedstrijdHelft);
        cmd.Parameters.AddWithValue("wr", i.WedstrijdRust);
        cmd.Parameters.AddWithValue("svt", i.StandaardVoorkeurTijd.HasValue
            ? i.StandaardVoorkeurTijd.Value.ToTimeSpan() : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("cc", clubCode);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> UpdateAsync(string leeftijd, SpeeltijdInput i, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        // #956: LOWER(...) i.p.v. kale '=' — Postgres' default tekstvergelijking is case-sensitive,
        // SQL Server's Latin1_General_CI_AS-collatie niet. Zonder deze wrap gaf een leeftijd met een
        // afwijkende hoofdlettering t.o.v. de opgeslagen sleutel hier stilzwijgend 0 bijgewerkte rijen.
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.speeltijden
            SET veldafmeting = @vf, wedstrijdtotaal = @wt, wedstrijdhelft = @wh, wedstrijdrust = @wr,
                standaardvoorkeurtijd = @svt
            WHERE LOWER(leeftijd) = LOWER(@l) AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("l", leeftijd);
        cmd.Parameters.AddWithValue("vf", i.Veldafmeting);
        cmd.Parameters.AddWithValue("wt", i.WedstrijdTotaal);
        cmd.Parameters.AddWithValue("wh", i.WedstrijdHelft);
        cmd.Parameters.AddWithValue("wr", i.WedstrijdRust);
        cmd.Parameters.AddWithValue("svt", i.StandaardVoorkeurTijd.HasValue
            ? i.StandaardVoorkeurTijd.Value.ToTimeSpan() : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<int> DeleteAsync(string leeftijd, string clubCode, string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        // #956: zelfde case-sensitiviteitsredenering als UpdateAsync hierboven.
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM public.speeltijden WHERE LOWER(leeftijd) = LOWER(@l) AND clubcode = @cc", conn);
        cmd.Parameters.AddWithValue("l", leeftijd);
        cmd.Parameters.AddWithValue("cc", clubCode);
        return await cmd.ExecuteNonQueryAsync();
    }
}
