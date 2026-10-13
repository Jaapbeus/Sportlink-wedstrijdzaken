using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Planner.Endpoints.Teambegeleiding;
using Planner.Shared.Teambegeleiding;
using SportlinkFunction.Admin;
using Xunit;

namespace FunctionApp.Tests.Admin;

/// <summary>
/// Echte-database-tests voor het inzagelog van teambegeleiding op de SQL Server-tier (#1701):
/// <see cref="SqlTeambegeleidingInzageStore"/> en <c>[avg].[sp_CleanupTeambegeleidingInzageLog]</c>.
/// Tegenhanger: <c>FunctionApp.Postgres.Tests/TeambegeleidingInzageIntegrationTests.cs</c>.
/// <para>
/// Lokaal draaien: start een wegwerp-SQL Server-container, voer <c>Database/avg/Tables/Teambegeleiding.sql</c>
/// (plus de kolom <c>Functie</c>) en het #1701-blok van <c>Database/Script.PostDeployment1.sql</c> uit in een
/// lege database en zet <c>SQLSERVER_TEST_CONNECTION_STRING</c> (nooit een wachtwoord in een bestand of log).
/// Zonder die variabele worden de tests overgeslagen. Testdata uitsluitend uit ARCHITECTUUR.md §8.1.5.
/// </para>
/// </summary>
public class SqlTeambegeleidingInzageStoreIntegrationTests
{
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)
        ?? throw new InvalidOperationException($"{SqlServerFactAttribute.EnvVar} niet gezet.");

    private static string NieuweClub() => "ztest" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(string sql, params (string, object?)[] parameters)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> TelAsync(string sql, params (string, object?)[] parameters)
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        foreach (var (naam, waarde) in parameters) cmd.Parameters.AddWithValue(naam, waarde ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task OpruimenAsync(string club)
    {
        await ExecAsync("DELETE FROM [avg].[Teambegeleiding] WHERE [ClubCode] = @c", ("@c", club));
        await ExecAsync("DELETE FROM [avg].[TeambegeleidingInzageLog] WHERE [ClubCode] = @c", ("@c", club));
    }

    private static Task BegeleiderAsync(string club, string team, string rol, string email) => ExecAsync(@"
        INSERT INTO [avg].[Teambegeleiding] ([Team], [Teamrol], [Naam], [Emailadres], [Telefoonnummer], [Functie], [ClubCode])
        VALUES (@team, @rol, N'Jan de Vries', @email, N'0000-000001', N'Technische staf', @c)",
        ("@team", team), ("@rol", rol), ("@email", email), ("@c", club));

    private static SqlTeambegeleidingInzageStore Store() => new(ConnectionString);

    [SqlServerFact]
    public async Task Tabel_HeeftAlleenKolommenVoorClubInzienerTeamAantalEnTijdstip()
    {
        using var conn = await OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT [name] FROM sys.columns WHERE [object_id] = OBJECT_ID('avg.TeambegeleidingInzageLog')", conn);
        var kolommen = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) kolommen.Add(reader.GetString(0));
        kolommen.Should().BeEquivalentTo(
            ["Id", "ClubCode", "InzienDoorObjectId", "InzienDoorNaam", "Team", "AantalRijen", "mta_inserted"],
            "het inzagelog bevat geen kolom voor naam, e-mailadres of telefoonnummer van een ingeziene begeleider");
    }

    [SqlServerFact]
    public async Task HaalContactgegevens_GeeftAlleenDeEigenClub_InRolvolgorde()
    {
        var club = NieuweClub();
        var andereClub = NieuweClub();
        try
        {
            await BegeleiderAsync(club, "JO11-1", "Teamleider", "teamleider@allstars-fc.test");
            await BegeleiderAsync(club, "JO11-1", "Trainer", "trainer@voorbeeld.nl");
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

    [SqlServerFact]
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
            (await TelAsync(@"SELECT COUNT(*) FROM [avg].[TeambegeleidingInzageLog]
                              WHERE [ClubCode] = @c AND [Team] = N'JO9-2' AND [AantalRijen] = 1
                                AND [InzienDoorObjectId] = N'oid-test' AND [InzienDoorNaam] = N'Beheerder Test'",
                ("@c", club))).Should().Be(1);
        }
        finally { await OpruimenAsync(club); }
    }

    [SqlServerFact]
    public async Task Retentie_VerwijdertRegelsOuderDanTwaalfMaanden_EnLaatJongereStaan()
    {
        var club = NieuweClub();
        try
        {
            var nu = DateTime.UtcNow;
            var wie = new TeambegeleidingInzager(null, null);
            await Store().LogInzageAsync(TeambegeleidingInzageNieuw.Maak(club, wie, "OUD", 1));
            await Store().LogInzageAsync(TeambegeleidingInzageNieuw.Maak(club, wie, "JONG", 1));
            await ExecAsync("UPDATE [avg].[TeambegeleidingInzageLog] SET [mta_inserted] = @t WHERE [ClubCode] = @c AND [Team] = N'OUD'",
                ("@t", nu.AddMonths(-13)), ("@c", club));
            await ExecAsync("UPDATE [avg].[TeambegeleidingInzageLog] SET [mta_inserted] = @t WHERE [ClubCode] = @c AND [Team] = N'JONG'",
                ("@t", nu.AddMonths(-11)), ("@c", club));

            using (var conn = await OpenAsync())
                (await SqlTeambegeleidingInzageStore.VerwijderVerlopenInzageAsync(conn, nu)).Should().BeGreaterThanOrEqualTo(1);

            (await TelAsync("SELECT COUNT(*) FROM [avg].[TeambegeleidingInzageLog] WHERE [ClubCode] = @c AND [Team] = N'OUD'", ("@c", club)))
                .Should().Be(0, "13 maanden is ouder dan de termijn van 12 maanden");
            (await TelAsync("SELECT COUNT(*) FROM [avg].[TeambegeleidingInzageLog] WHERE [ClubCode] = @c AND [Team] = N'JONG'", ("@c", club)))
                .Should().Be(1, "11 maanden valt nog binnen de termijn");
        }
        finally { await OpruimenAsync(club); }
    }
}
