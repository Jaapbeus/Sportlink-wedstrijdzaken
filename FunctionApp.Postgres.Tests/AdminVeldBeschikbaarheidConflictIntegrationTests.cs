using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Admin;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests;

public class AdminVeldBeschikbaarheidConflictIntegrationTests
{
    [PostgresFact]
    public async Task BestaatAndereVoorUpdate_ConflicterendePeriode_GeeftTrueEnAnderePeriodeFalse()
    {
        var club = "z1819" + Guid.NewGuid().ToString("N")[..8];
        var veld = Random.Shared.Next(100_000_000, 2_000_000_000);
        var cs = PostgresTestEnvironment.ConnectionStringOrNull!;
        var periodeA = 0;
        var periodeB = 0;
        var idA = 0;
        var idB = 0;

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using (var veldCmd = new NpgsqlCommand(@"
                INSERT INTO public.velden (veldnummer, veldnaam, veldtype, heeftkunstlicht, actief, clubcode)
                VALUES (@veld, 'Testveld', 'kunstgras', FALSE, TRUE, @club);", conn))
            {
                veldCmd.Parameters.AddWithValue("veld", veld);
                veldCmd.Parameters.AddWithValue("club", club);
                await veldCmd.ExecuteNonQueryAsync();
            }
            periodeA = await VoegPeriodeAAsync(conn, club);
            periodeB = await VoegPeriodeToeAsync(conn, club, "2026-07-01", "2026-12-31");
            idA = await VoegBeschikbaarheidToeAsync(conn, veld, periodeA, club);
            idB = await VoegBeschikbaarheidToeAsync(conn, veld, periodeB, club);
        }

        try
        {
            (await AdminVeldBeschikbaarheidRepository.BestaatAndereVoorUpdateAsync(idB, periodeA, club, cs)).Should().BeTrue();
            (await AdminVeldBeschikbaarheidRepository.BestaatAndereVoorUpdateAsync(idB, periodeB, club, cs)).Should().BeFalse();
        }
        finally
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                DELETE FROM public.veldbeschikbaarheid WHERE clubcode = @club;
                DELETE FROM public.veldperiode WHERE clubcode = @club;
                DELETE FROM public.velden WHERE veldnummer = @veld AND clubcode = @club;", conn);
            cmd.Parameters.AddWithValue("club", club);
            cmd.Parameters.AddWithValue("veld", veld);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> VoegPeriodeAAsync(NpgsqlConnection conn, string club)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.veldperiode (naam, datumvan, datumtot, clubcode)
            VALUES ('Periode A', '2026-01-01', '2026-06-30', @club)
            RETURNING id;", conn);
        cmd.Parameters.AddWithValue("club", club);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<int> VoegPeriodeToeAsync(NpgsqlConnection conn, string club, string van, string tot)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.veldperiode (naam, datumvan, datumtot, clubcode)
            VALUES ('Periode B', @van, @tot, @club)
            RETURNING id;", conn);
        cmd.Parameters.AddWithValue("van", DateOnly.Parse(van));
        cmd.Parameters.AddWithValue("tot", DateOnly.Parse(tot));
        cmd.Parameters.AddWithValue("club", club);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<int> VoegBeschikbaarheidToeAsync(NpgsqlConnection conn, int veld, int periode, string club)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.veldbeschikbaarheid
                (veldnummer, dagvanweek, beschikbaarvanaf, beschikbaartot, gebruikzonsondergang, periodeid, clubcode)
            VALUES (@veld, 6, '18:00', '19:00', FALSE, @periode, @club)
            RETURNING id;", conn);
        cmd.Parameters.AddWithValue("veld", veld);
        cmd.Parameters.AddWithValue("periode", periode);
        cmd.Parameters.AddWithValue("club", club);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
