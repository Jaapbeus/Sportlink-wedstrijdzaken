using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using SportlinkFunction.Planner;
using Xunit;

namespace FunctionApp.Tests.Planner;

/// <summary>
/// #1911: de leesproef van <c>/api/health</c> en de speeltijden-lookup tegen een echte SQL Server — gelijk aan de
/// Postgres-tier. SqlClient levert een <c>time</c>-kolom als <see cref="TimeSpan"/>, Npgsql 10 als
/// <see cref="TimeOnly"/>; de gedeelde <c>Speeltijd.FromReader</c> moet beide aankunnen.
/// Opzet: <c>SQLSERVER_TEST_CONNECTION_STRING</c> naar een database met <c>dbo.Speeltijden</c> (CI-job
/// "PostDeployment op verse database"). Testdata synthetisch, eigen testclub.
/// </summary>
public class PlannerSpeeltijdenLeesPadIntegrationTests
{
    private const string Club = "ZTIJD1911";
    private static string Cs => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;

    [SqlServerFact]
    public async Task Lookup_RijMetVoorkeurstijd_LeestTimeKolomEnProbeGeeftTrue()
    {
        await using var conn = new SqlConnection(Cs);
        await conn.OpenAsync();
        await Voer(conn, "DELETE FROM [dbo].[Speeltijden] WHERE [ClubCode] = @cc");
        await Voer(conn,
            @"INSERT INTO [dbo].[Speeltijden] ([Leeftijd],[Veldafmeting],[WedstrijdTotaal],[WedstrijdHelft],[WedstrijdRust],[StandaardVoorkeurTijd],[ClubCode])
              VALUES ('JO9', 0.5, 60, 30, 10, '10:30', @cc), ('JO11', 0.5, 70, 35, 10, NULL, @cc)");
        try
        {
            var lookup = await PlannerSettingsRepository.GetSpeeltijdenLookupAsync(conn, null, Club);
            lookup["JO9"].StandaardVoorkeurTijd.Should().Be(new TimeOnly(10, 30));
            lookup["JO11"].StandaardVoorkeurTijd.Should().BeNull();

            (await PlannerSettingsRepository.ProbeSpeeltijdenLeesPadAsync(Cs, CancellationToken.None))
                .Should().BeTrue();
        }
        finally
        {
            await Voer(conn, "DELETE FROM [dbo].[Speeltijden] WHERE [ClubCode] = @cc");
        }
    }

    [Fact]
    public async Task Probe_OnbereikbareDatabase_GeeftFoutDoorAanHealth()
    {
        // Health vangt dit en meldt readPathsOk=false (degraded); de probe zelf slikt nooit een fout in.
        var act = () => PlannerSettingsRepository.ProbeSpeeltijdenLeesPadAsync(
            "Server=127.0.0.1,1;Database=x;User Id=x;Password=x;Connect Timeout=2;Encrypt=False", CancellationToken.None);
        await act.Should().ThrowAsync<SqlException>();
    }

    private static async Task Voer(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@cc", Club);
        await cmd.ExecuteNonQueryAsync();
    }
}
