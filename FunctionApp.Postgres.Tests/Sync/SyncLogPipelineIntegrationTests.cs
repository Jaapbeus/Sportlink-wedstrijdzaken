using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using global::Planner.Shared.Sync;
using Database.Postgres;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Sync;
using FunctionApp.Tests.Sync;
using Npgsql;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

/// <summary>Werkelijke fase-uitkomsten en merge-aantallen via een lokale fixtureserver.</summary>
public class SyncLogPipelineIntegrationTests
{
    private const string Club = "ALLSTARS-LOG";
    private static string Cs => PostgresTestEnvironment.ConnectionStringOrNull!;

    [PostgresFact]
    public async Task FixturePipeline_RecordsEveryPhase_AndCountsOnlyActualChanges()
    {
        await HisTabelVorm.ZorgVoorProductievormAsync(Cs, KnownEntities.Teams, KnownEntities.Matches, KnownEntities.MatchDetails);
        await CleanupAsync();
        using var server = SportlinkFixtures.BuildServer(90001912, Club);
        try
        {
            // Andere fixturetests gebruiken dezelfde teamsleutel; clubcode behoort niet tot die sleutel.
            // Geef deze teltest een eigen sleutel, zodat de eerste run ook na andere tests een insert is.
            using var fixtureClient = new HttpClient();
            var teams = System.Text.Json.Nodes.JsonNode.Parse(await fixtureClient.GetStringAsync(server.BaseUrl + "/teams"))!;
            teams[0]!["teamcode"] = 90001912;
            server.RespondWithJson("/teams", teams.ToJsonString());
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
        await using var heldLease = callerOwnsLease ? await PostgresSyncStagingLease.TryAcquireAsync(Cs) : null;
        if (callerOwnsLease) heldLease.Should().NotBeNull();
        var outcome = await PostgresSyncPipeline.RunSyncAsync(0, 0, server.BaseUrl, "clientId=fixture-test", Club, Cs, NullLogger.Instance, syncLog: writer, acquiredLease: heldLease);
        if (callerOwnsLease)
        {
            await using var competingLease = await PostgresSyncStagingLease.TryAcquireAsync(Cs);
            competingLease.Should().BeNull("the pipeline must not dispose its caller's lease");
        }
        outcome.GedeeltelijkMislukt.Should().Be(expectedPartial);
        if (expectedPartial) writer.HasIncompletePhases.Should().BeTrue();
        return entries;
    }

    private static async Task CleanupAsync()
    {
        await using var connection = new NpgsqlConnection(Cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM his.matchdetails WHERE clubcode=@club; DELETE FROM his.matches WHERE clubcode=@club; DELETE FROM his.teams WHERE clubcode=@club;", connection);
        command.Parameters.AddWithValue("@club", Club);
        await command.ExecuteNonQueryAsync();
    }
}
