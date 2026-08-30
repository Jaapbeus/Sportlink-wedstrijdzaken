using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction;
using Xunit;

namespace FunctionApp.Tests.Sync;

/// <summary>
/// End-to-end synchronisatietest tegen een lokale fixtureserver in plaats van de echte
/// Sportlink-API (#867). Vereist een echte, lege SQL Server-database met het volledige schema
/// (<c>Database/Script.PostDeployment1.sql</c> uitgevoerd) — lokaal uitvoeren tegen een
/// wegwerpcontainer, exact zoals de CI-job "PostDeployment op verse database"
/// (<c>.github/workflows/build.yml</c>) dat al doet:
///
///   docker run -d --name sqlfixture -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=Devonly123! -e MSSQL_PID=Developer -p 1434:1433 mcr.microsoft.com/mssql/server:2022-latest
///   # wacht tot de container klaar is (`docker exec sqlfixture /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P Devonly123! -C -Q "SELECT 1"`)
///   docker exec sqlfixture /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P Devonly123! -C -Q "CREATE DATABASE SportlinkFixture"
///   docker cp Database/Script.PostDeployment1.sql sqlfixture:/tmp/postdeployment.sql
///   docker exec sqlfixture /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P Devonly123! -C -d SportlinkFixture -b -V 11 -i /tmp/postdeployment.sql
///   $env:SqlConnectionString = "Server=localhost,1434;Database=SportlinkFixture;User Id=sa;Password=Devonly123!;TrustServerCertificate=True;"
///   dotnet test FunctionApp.Tests --filter FullyQualifiedName~SportlinkFixtureSyncIntegrationTests
///   docker rm -f sqlfixture
///
/// <para>
/// <b>"Geen enkele externe dienst geraakt" — bewezen, niet aangenomen (#867):</b>
/// <see cref="SportlinkSyncPipeline.RunSyncAsync"/> krijgt <c>sportlinkApiUrl</c> uitsluitend mee
/// als parameter (zie klasse-doc-comment aldaar) — er is geen ander pad waarlangs de pipeline een
/// andere host zou kunnen bereiken. Deze test geeft daar bewust het adres van
/// <see cref="SportlinkFixtureServer"/> aan mee, en verifieert na afloop expliciet welke paden de
/// fixtureserver daadwerkelijk binnenkreeg (<see cref="SportlinkFixtureServer.Requests"/>) —
/// controleerbaar zonder de Sportlink-API zelf te hoeven vertrouwen.
/// </para>
/// <para>
/// <b>Statische instellingencache (#867):</b> <see cref="SportlinkSyncPipeline.RunSyncAsync"/>
/// leest <c>clubCode</c> uit <c>SystemUtilities.AppSettings</c>' procesbrede, statische cache — deze
/// test zet die rechtstreeks via <see cref="SystemUtilities.AppSettings.SetForTests"/> (geen
/// databaseoproep nodig) en ruimt hem in <see cref="DisposeAsync"/> weer op. Zie
/// <c>FunctionApp.Tests/AssemblyInfo.cs</c> voor waarom dit test-parallellisme project-breed uitschakelt.
/// </para>
/// </summary>
public class SportlinkFixtureSyncIntegrationTests : IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "SqlConnectionString";
    private const string ClubCode = "testclub";
    private const long Wedstrijdcode = 90000001;

    private string ConnectionString => Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
        ?? throw new InvalidOperationException($"{ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    public async Task InitializeAsync()
    {
        SystemUtilities.AppSettings.SetForTests("clubCode", ClubCode);

        // Schone lei: eerdere staging-/his-rijen van een vorige lokale run mogen deze test niet
        // beïnvloeden (CreateStagingTable dropt/hermaakt stg zelf al, maar his.* blijft staan).
        // his.teams/his.matches/his.matchdetails bestaan op een verse database nog helemaal niet —
        // sp_CreateTargetTableFromSource maakt ze pas aan bij de EERSTE MergeStgToHis-aanroep — dus
        // elke DELETE is voorwaardelijk op het daadwerkelijk bestaan van de tabel.
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cleanup = new SqlCommand(
            "IF OBJECT_ID('[his].[matchdetails]') IS NOT NULL DELETE FROM [his].[matchdetails] WHERE WedstrijdCode = @code; " +
            "IF OBJECT_ID('[his].[matches]') IS NOT NULL DELETE FROM [his].[matches] WHERE wedstrijdcode = @code; " +
            "IF OBJECT_ID('[his].[teams]') IS NOT NULL DELETE FROM [his].[teams] WHERE ClubCode = @club;", connection);
        cleanup.Parameters.AddWithValue("@code", Wedstrijdcode);
        cleanup.Parameters.AddWithValue("@club", ClubCode);
        await cleanup.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        SystemUtilities.AppSettings.ResetForTests();
        return Task.CompletedTask;
    }

    [Fact(Skip = "Vereist lokale SQL Server met volledig schema (zie klasse-doc-comment) — lokaal uitvoeren tegen een wegwerpcontainer")]
    public async Task RunSyncAsync_TegenFixtureserver_IsIdempotentEnRaaktUitsluitendDeFixture()
    {
        using var fixtureServer = SportlinkFixtures.BuildServer(Wedstrijdcode, ClubCode);

        // Kleine week-range: minimaliseert het aantal fixture-aanroepen zonder de aard van de test
        // te veranderen (elke weekoffset krijgt toch hetzelfde canned antwoord — zie
        // SportlinkFixtureServer.RespondWithJson).
        await SportlinkSyncPipeline.RunSyncAsync(
            fromWeekOffset: 0, toWeekOffset: 0,
            sportlinkApiUrl: fixtureServer.BaseUrl,
            sportlinkClientId: "clientId=fixture-test",
            log: NullLogger.Instance);

        // ── Bewijs 1: uitsluitend de fixture is geraakt, en wel op de verwachte endpoints ──
        fixtureServer.Requests.Should().Contain(r => r.StartsWith("/teams"));
        fixtureServer.Requests.Should().Contain(r => r.StartsWith("/programma"));
        fixtureServer.Requests.Should().Contain(r => r.StartsWith("/uitslagen"));
        fixtureServer.Requests.Should().Contain(r => r.StartsWith("/wedstrijd-informatie") && r.Contains(Wedstrijdcode.ToString()));

        // ── Bewijs 2: de wedstrijd, het team en de matchdetails staan daadwerkelijk in his.* ──
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var firstModified = await ReadMtaModifiedAsync(connection,
            "SELECT mta_modified FROM [his].[matches] WHERE wedstrijdcode = @code", Wedstrijdcode);
        firstModified.Should().NotBeNull("de wedstrijd uit /programma moet na de eerste sync in his.matches staan");

        var teamCount = await CountAsync(connection,
            "SELECT COUNT(*) FROM [his].[teams] WHERE ClubCode = @club", ClubCode);
        teamCount.Should().Be(1, "het ene team uit /teams moet na de eerste sync in his.teams staan");

        var detailCount = await CountAsync(connection,
            "SELECT COUNT(*) FROM [his].[matchdetails] WHERE WedstrijdCode = @code", Wedstrijdcode);
        detailCount.Should().Be(1, "de matchdetails uit /wedstrijd-informatie moeten na de eerste sync in his.matchdetails staan");

        // ── Bewijs 3 (kernacceptatiecriterium #867): tweemaal draaien tegen identieke fixture-data
        // levert geen duplicaten op en verandert mta_modified niet voor ongewijzigde rijen ──
        await Task.Delay(50); // zorg dat GETUTCDATE() daadwerkelijk vooruitgaat tussen de twee runs
        fixtureServer.Requests.Clear();

        await SportlinkSyncPipeline.RunSyncAsync(
            fromWeekOffset: 0, toWeekOffset: 0,
            sportlinkApiUrl: fixtureServer.BaseUrl,
            sportlinkClientId: "clientId=fixture-test",
            log: NullLogger.Instance);

        var secondModified = await ReadMtaModifiedAsync(connection,
            "SELECT mta_modified FROM [his].[matches] WHERE wedstrijdcode = @code", Wedstrijdcode);
        secondModified.Should().Be(firstModified,
            "identieke brondata mag mta_modified niet bijwerken (sp_MergeStgToHis.sql: UPDATE alleen bij daadwerkelijk gewijzigde kolommen)");

        var teamCountAfterSecondRun = await CountAsync(connection,
            "SELECT COUNT(*) FROM [his].[teams] WHERE ClubCode = @club", ClubCode);
        teamCountAfterSecondRun.Should().Be(1, "een tweede run met identieke data mag geen duplicaatrij toevoegen");

        var detailCountAfterSecondRun = await CountAsync(connection,
            "SELECT COUNT(*) FROM [his].[matchdetails] WHERE WedstrijdCode = @code", Wedstrijdcode);
        detailCountAfterSecondRun.Should().Be(1, "een tweede run met identieke data mag geen duplicaatrij toevoegen");
    }

    private static async Task<DateTime?> ReadMtaModifiedAsync(SqlConnection connection, string sql, long code)
    {
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@code", code);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : (DateTime)result;
    }

    private static async Task<int> CountAsync(SqlConnection connection, string sql, object param)
    {
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(sql.Contains("@club") ? "@club" : "@code", param);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
