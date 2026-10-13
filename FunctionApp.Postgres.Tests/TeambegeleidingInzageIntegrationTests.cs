using AwesomeAssertions;
using Database.Postgres;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Teambegeleiding;
using Npgsql;
using Planner.Shared.Teambegeleiding;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>
/// Echte-database-tests voor het inzagelog van teambegeleiding op de Postgres-tier (#1701):
/// <see cref="PostgresTeambegeleidingInzageStore"/>, migratie 042 en
/// <see cref="PostgresCleanupProcedures.CleanupTeambegeleidingInzageLogAsync"/>. Tegenhanger:
/// <c>FunctionApp.Tests/Admin/SqlTeambegeleidingInzageStoreIntegrationTests.cs</c>.
/// <para>
/// Elke test werkt onder een eigen, unieke clubcode en ruimt zijn rijen op. Testdata uitsluitend uit
/// ARCHITECTUUR.md §8.1.5.
/// </para>
/// </summary>
public class TeambegeleidingInzageIntegrationTests
{
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException($"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet.");

    private static string NieuweClub() => "ztest" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(string sql, params (string, object?)[] parameters)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> TelAsync(string sql, params (string, object?)[] parameters)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde ?? DBNull.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task OpruimenAsync(string club)
    {
        await ExecAsync("DELETE FROM avg.teambegeleiding WHERE clubcode = @c", ("c", club));
        await ExecAsync("DELETE FROM avg.teambegeleidinginzagelog WHERE clubcode = @c", ("c", club));
    }

    private static Task BegeleiderAsync(string club, string team, string rol, string email) => ExecAsync(@"
        INSERT INTO avg.teambegeleiding (team, teamrol, naam, emailadres, telefoonnummer, functie, clubcode)
        VALUES (@team, @rol, 'Jan de Vries', @email, '0000-000001', 'Technische staf', @c)",
        ("team", team), ("rol", rol), ("email", email), ("c", club));

    private static PostgresTeambegeleidingInzageStore Store() => new(ConnectionString);

    [PostgresFact]
    public async Task Tabel_HeeftAlleenKolommenVoorClubInzienerTeamAantalEnTijdstip_EnRlsAan()
    {
        await using var conn = await OpenAsync();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'avg' AND table_name = 'teambegeleidinginzagelog'", conn))
        {
            var kolommen = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) kolommen.Add(reader.GetString(0));
            kolommen.Should().BeEquivalentTo(
                ["id", "clubcode", "inziendoorobjectid", "inziendoornaam", "team", "aantalrijen", "mta_inserted"],
                "het inzagelog bevat geen kolom voor naam, e-mailadres of telefoonnummer van een ingeziene begeleider");
        }

        await using var rls = new NpgsqlCommand(
            "SELECT relrowsecurity FROM pg_class WHERE oid = 'avg.teambegeleidinginzagelog'::regclass", conn);
        ((bool)(await rls.ExecuteScalarAsync())!).Should().BeTrue("#1198: RLS aan in dezelfde migratie");
    }

    [PostgresFact]
    public async Task HaalContactgegevens_GeeftAlleenDeEigenClub_InRolvolgorde()
    {
        var club = NieuweClub();
        var andereClub = NieuweClub();
        try
        {
            await BegeleiderAsync(club, "JO11-1", "Teamleider", "teamleider@allstars-fc.test");
            await BegeleiderAsync(club, "JO11-1", "trainer", "trainer@voorbeeld.nl");   // kleine letters: ILIKE
            await BegeleiderAsync(andereClub, "JO11-1", "Trainer", "trainer@allstars-fc.test");

            var contacten = await Store().HaalContactgegevensAsync(club, "JO11-1");

            contacten.Select(c => c.Emailadres).Should().Equal("trainer@voorbeeld.nl", "teamleider@allstars-fc.test");
            contacten[0].Telefoonnummer.Should().Be("0000-000001");
            contacten[0].Functie.Should().Be("Technische staf");
        }
        finally
        {
            await OpruimenAsync(club);
            await OpruimenAsync(andereClub);
        }
    }

    [PostgresFact]
    public async Task LogInzage_SchrijftEenRegelMetInzienerTeamEnAantal_ZonderGegevensVanBegeleiders()
    {
        var club = NieuweClub();
        try
        {
            await Store().LogInzageAsync(TeambegeleidingInzageNieuw.Maak(
                club, new TeambegeleidingInzager("00000000-0000-0000-0000-00000000000a", "Beheerder Test"), "JO11-1", 2));

            await using var conn = await OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT inziendoorobjectid, inziendoornaam, team, aantalrijen, mta_inserted
                FROM avg.teambegeleidinginzagelog WHERE clubcode = @c", conn);
            cmd.Parameters.AddWithValue("c", club);
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("00000000-0000-0000-0000-00000000000a");
            reader.GetString(1).Should().Be("Beheerder Test");
            reader.GetString(2).Should().Be("JO11-1");
            reader.GetInt32(3).Should().Be(2);
            reader.GetDateTime(4).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
            (await reader.ReadAsync()).Should().BeFalse("precies één regel");
        }
        finally { await OpruimenAsync(club); }
    }

    [PostgresFact]
    public async Task Opvragen_ViaDeGedeeldeKern_GeeftGegevensEnLegtEenInzageregelVast()
    {
        var club = NieuweClub();
        try
        {
            await BegeleiderAsync(club, "JO9-2", "Trainer", "trainer@voorbeeld.nl");

            var resultaat = await TeambegeleidingInzageEndpointCore.ContactgegevensAsync(
                club, "JO9-2", new TeambegeleidingInzager("oid-test", "Beheerder Test"), Store());

            resultaat.Should().BeOfType<OkObjectResult>()
                .Which.Value.Should().BeAssignableTo<IReadOnlyList<TeambegeleiderContact>>().Which.Should().ContainSingle();
            (await TelAsync("SELECT COUNT(*) FROM avg.teambegeleidinginzagelog WHERE clubcode = @c AND team = 'JO9-2' AND aantalrijen = 1 AND inziendoorobjectid = 'oid-test'",
                ("c", club))).Should().Be(1);
        }
        finally { await OpruimenAsync(club); }
    }

    [PostgresFact]
    public async Task Retentie_VerwijdertRegelsOuderDanTwaalfMaanden_EnLaatJongereStaan()
    {
        var club = NieuweClub();
        try
        {
            var nu = DateTime.UtcNow;
            var wie = new TeambegeleidingInzager(null, null);
            await Store().LogInzageAsync(TeambegeleidingInzageNieuw.Maak(club, wie, "OUD", 1));
            await Store().LogInzageAsync(TeambegeleidingInzageNieuw.Maak(club, wie, "JONG", 1));
            await ExecAsync("UPDATE avg.teambegeleidinginzagelog SET mta_inserted = @t WHERE clubcode = @c AND team = 'OUD'",
                ("t", nu.AddMonths(-13)), ("c", club));
            await ExecAsync("UPDATE avg.teambegeleidinginzagelog SET mta_inserted = @t WHERE clubcode = @c AND team = 'JONG'",
                ("t", nu.AddMonths(-11)), ("c", club));

            await using (var conn = await OpenAsync())
            {
                var verwijderd = await PostgresCleanupProcedures.CleanupTeambegeleidingInzageLogAsync(
                    conn, nu, TeambegeleidingInzageRetentie.InzageLogMaanden);
                verwijderd.Should().BeGreaterThanOrEqualTo(1);
            }

            (await TelAsync("SELECT COUNT(*) FROM avg.teambegeleidinginzagelog WHERE clubcode = @c AND team = 'OUD'", ("c", club)))
                .Should().Be(0, "13 maanden is ouder dan de termijn van 12 maanden");
            (await TelAsync("SELECT COUNT(*) FROM avg.teambegeleidinginzagelog WHERE clubcode = @c AND team = 'JONG'", ("c", club)))
                .Should().Be(1, "11 maanden valt nog binnen de termijn");
        }
        finally { await OpruimenAsync(club); }
    }
}
