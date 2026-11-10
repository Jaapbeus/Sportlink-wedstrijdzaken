using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

public class AdminVeldBeschikbaarheidConflictIntegrationTests
{
    [SqlServerFact]
    public async Task BestaatAndereVoorUpdate_ConflicterendePeriode_GeeftTrueEnAnderePeriodeFalse()
    {
        var club = "z1819" + Guid.NewGuid().ToString("N")[..8];
        var veld = Random.Shared.Next(100_000_000, 2_000_000_000);
        var cs = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;
        var periodeA = 0;
        var periodeB = 0;
        var idA = 0;
        var idB = 0;

        try
        {
            await using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                await using (var cmd = new SqlCommand(@"
                INSERT INTO [dbo].[Velden] ([VeldNummer], [VeldNaam], [VeldType], [HeeftKunstlicht], [Actief], [ClubCode])
                VALUES (@veld, N'Testveld', N'kunstgras', 0, 1, @club);
                INSERT INTO [dbo].[VeldPeriode] ([Naam], [DatumVan], [DatumTot], [ClubCode])
                OUTPUT INSERTED.[Id] VALUES (N'Periode A', '2026-01-01', '2026-06-30', @club);", conn))
                {
                    cmd.Parameters.AddWithValue("@veld", veld);
                    cmd.Parameters.AddWithValue("@club", club);
                    periodeA = (int)(await cmd.ExecuteScalarAsync())!;
                }
                periodeB = await VoegPeriodeToeAsync(conn, club, "2026-07-01", "2026-12-31");
                idA = await VoegBeschikbaarheidToeAsync(conn, veld, periodeA, club);
                idB = await VoegBeschikbaarheidToeAsync(conn, veld, periodeB, club);
            }

            (await AdminVeldBeschikbaarheidRepository.BestaatAndereVoorUpdateAsync(idB, periodeA, club, cs)).Should().BeTrue();
            (await AdminVeldBeschikbaarheidRepository.BestaatAndereVoorUpdateAsync(idB, periodeB, club, cs)).Should().BeFalse();
            (await AdminVeldBeschikbaarheidRepository.BestaatAndereVoorUpdateAsync(idA, periodeA, club, cs)).Should().BeFalse();
            (await AdminVeldBeschikbaarheidRepository.BestaatAndereVoorUpdateAsync(idA, periodeA, club + "X", cs)).Should().BeFalse();
        }
        finally
        {
            await VerwijderTestdataAsync(club, veld, cs);
        }
    }

    [SqlServerFact]
    public async Task MislukteSetup_NaInvoegenVeld_LaatGeenTestdataAchter()
    {
        var club = "z1819" + Guid.NewGuid().ToString("N")[..8];
        var veld = Random.Shared.Next(100_000_000, 2_000_000_000);
        var cs = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;

        var fout = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            try
            {
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync();
                await using (var cmd = new SqlCommand(@"
                    INSERT INTO [dbo].[Velden] ([VeldNummer], [VeldNaam], [VeldType], [HeeftKunstlicht], [Actief], [ClubCode])
                    VALUES (@veld, N'Testveld', N'kunstgras', 0, 1, @club);", conn))
                {
                    cmd.Parameters.AddWithValue("@veld", veld);
                    cmd.Parameters.AddWithValue("@club", club);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Fail after the first row commits to prove setup failures still enter cleanup.
                await using var invalidSetup = new SqlCommand("INSERT INTO [dbo].[TabelBestaatNiet] DEFAULT VALUES;", conn);
                await invalidSetup.ExecuteNonQueryAsync();
            }
            finally
            {
                await VerwijderTestdataAsync(club, veld, cs);
            }
        });

        fout.Message.Should().Contain("TabelBestaatNiet");
        await using var verify = new SqlConnection(cs);
        await verify.OpenAsync();
        await using var count = new SqlCommand(@"
            SELECT
                (SELECT COUNT(*) FROM [dbo].[Velden] WHERE [VeldNummer] = @veld AND [ClubCode] = @club) +
                (SELECT COUNT(*) FROM [dbo].[VeldPeriode] WHERE [ClubCode] = @club) +
                (SELECT COUNT(*) FROM [dbo].[VeldBeschikbaarheid] WHERE [ClubCode] = @club);", verify);
        count.Parameters.AddWithValue("@club", club);
        count.Parameters.AddWithValue("@veld", veld);
        (await count.ExecuteScalarAsync()).Should().Be(0);
    }

    private static async Task VerwijderTestdataAsync(string club, int veld, string cs)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            DELETE FROM [dbo].[VeldBeschikbaarheid] WHERE [ClubCode] = @club;
            DELETE FROM [dbo].[VeldPeriode] WHERE [ClubCode] = @club;
            DELETE FROM [dbo].[Velden] WHERE [VeldNummer] = @veld AND [ClubCode] = @club;", conn);
        cmd.Parameters.AddWithValue("@club", club);
        cmd.Parameters.AddWithValue("@veld", veld);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> VoegPeriodeToeAsync(SqlConnection conn, string club, string van, string tot)
    {
        await using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[VeldPeriode] ([Naam], [DatumVan], [DatumTot], [ClubCode])
            OUTPUT INSERTED.[Id] VALUES (N'Periode B', @van, @tot, @club);", conn);
        cmd.Parameters.AddWithValue("@van", DateOnly.Parse(van));
        cmd.Parameters.AddWithValue("@tot", DateOnly.Parse(tot));
        cmd.Parameters.AddWithValue("@club", club);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<int> VoegBeschikbaarheidToeAsync(SqlConnection conn, int veld, int periode, string club)
    {
        await using var cmd = new SqlCommand(@"
            INSERT INTO [dbo].[VeldBeschikbaarheid]
                ([VeldNummer], [DagVanWeek], [BeschikbaarVanaf], [BeschikbaarTot], [GebruikZonsondergang], [PeriodeId], [ClubCode])
            OUTPUT INSERTED.[Id] VALUES (@veld, 6, '18:00', '19:00', 0, @periode, @club);", conn);
        cmd.Parameters.AddWithValue("@veld", veld);
        cmd.Parameters.AddWithValue("@periode", periode);
        cmd.Parameters.AddWithValue("@club", club);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
