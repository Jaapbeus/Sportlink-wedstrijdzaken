using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using global::Planner.Shared.Sync;
using SportlinkFunction;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FunctionApp.Tests.Sync;

/// <summary>Werkelijke fase-uitkomsten en merge-aantallen via een lokale fixtureserver.</summary>
public class SqlServerProgrammaPlafondSyncIntegrationTests_Logboek
{
    private const string Club = "ALLSTARS-LOG";
    private static string Cs => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;

    [SqlServerFact]
    public async Task FixturePipeline_RecordsEveryPhase_AndCountsOnlyActualChanges()
    {
        SystemUtilities.AppSettings.SetForTests("clubCode", Club);
        await CleanupAsync();
        using var server = SportlinkFixtures.BuildServer(90001912, Club);
        try
        {
            var first = await RunAsync(server);
            first.Keys.Should().BeEquivalentTo(new[] { "teams", "programma", "uitslagen", "matchdetails", "reconciliatie", "planner-view", "canonicalisatie" });
            first.Values.Should().OnlyContain(e => e.CompletedAt.HasValue && e.Status != "running");
            first["teams"].Fetched.Should().Be(1);
            first["teams"].Added.Should().Be(1);
            first["teams"].Updated.Should().Be(0);
            first["programma"].Added.Should().Be(1);
            first["matchdetails"].Fetched.Should().Be(1);
            first["matchdetails"].Added.Should().Be(1);
            server.Requests.Should().Contain(r => r.StartsWith("/teams"));
            server.Requests.Should().Contain(r => r.StartsWith("/wedstrijd-informatie"));

            var repeated = await RunAsync(server, callerOwnsLease: true);
            foreach (var phase in new[] { "teams", "programma", "matchdetails" })
            {
                repeated[phase].Added.Should().Be(0);
                repeated[phase].Updated.Should().Be(0);
            }

            using var client = new HttpClient();
            using var source = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(server.BaseUrl + "/programma"));
            var row = source.RootElement[0].GetRawText();
            server.RespondWithJson("/programma", "[" + string.Join(",", Enumerable.Repeat(row, ProgrammaAfkapping.Plafond)) + "]");
            var incomplete = await RunAsync(server, expectedPartial: true);
            incomplete["programma"].Status.Should().Be("partial");
            incomplete["programma"].ReasonCode.Should().Be("programme_truncated");
            incomplete["programma"].Fetched.Should().BeGreaterThanOrEqualTo(ProgrammaAfkapping.Plafond);
            incomplete["reconciliatie"].ReasonCode.Should().Be("reconciliation_skipped");
        }
        finally
        {
            await CleanupAsync();
            SystemUtilities.AppSettings.ResetForTests();
        }
    }

    private static async Task<Dictionary<string, SyncLogEntry>> RunAsync(SportlinkFixtureServer server, bool expectedPartial = false, bool callerOwnsLease = false)
    {
        var entries = new Dictionary<string, SyncLogEntry>();
        var writer = new SyncLogWriter(Guid.NewGuid(), 1, entry =>
        {
            entries[entry.Phase] = entry;
            return Task.CompletedTask;
        });
        await using var heldLease = callerOwnsLease ? await SqlServerSyncStagingLease.TryAcquireAsync() : null;
        if (callerOwnsLease) heldLease.Should().NotBeNull();
        var outcome = await SportlinkSyncPipeline.RunSyncWithSeasonWindowAsync(() => Task.FromResult((0, 0)), server.BaseUrl, "clientId=fixture-test", NullLogger.Instance, syncLog: writer, acquiredLease: heldLease);
        if (callerOwnsLease)
        {
            await using var competingLease = await SqlServerSyncStagingLease.TryAcquireAsync();
            competingLease.Should().BeNull("the pipeline must not dispose its caller's lease");
        }
        outcome.GedeeltelijkMislukt.Should().Be(expectedPartial);
        if (expectedPartial) writer.HasIncompletePhases.Should().BeTrue();
        return entries;
    }

    private static async Task CleanupAsync()
    {
        await using var connection = new SqlConnection(Cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand("IF OBJECT_ID('his.matchdetails') IS NOT NULL DELETE FROM his.matchdetails WHERE ClubCode=@club; IF OBJECT_ID('his.matches') IS NOT NULL DELETE FROM his.matches WHERE ClubCode=@club; IF OBJECT_ID('his.teams') IS NOT NULL DELETE FROM his.teams WHERE ClubCode=@club;", connection);
        command.Parameters.AddWithValue("@club", Club);
        await command.ExecuteNonQueryAsync();
    }
}
