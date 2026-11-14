using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Sync;
using FunctionApp.Tests.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Planner.Shared.Sync;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

public sealed class PostgresSyncStagingLeaseIntegrationTests
{
    private const string MergeFailureConstraint = "ck_1818_sync_merge_failure";
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull!;

    [PostgresFact]
    public async Task TrackedStart_LeaseCollision_DoesNotBuildWindowOrCreateFailedJob()
    {
        var clubCode = TestClubCode();
        var seasonTouched = false;
        var windowBuilt = false;
        await using var lease = await PostgresSyncStagingLease.TryAcquireAsync(ConnectionString);
        lease.Should().NotBeNull();

        var run = () => SyncFunction.RunTrackedSyncAsync(
            () => { windowBuilt = true; return Task.FromResult((0, 0)); },
            NullLogger.Instance, clubCode,
            () => { seasonTouched = true; return Task.CompletedTask; },
            ConnectionString);
        await run.Should().ThrowAsync<SyncAlreadyRunningException>();
        seasonTouched.Should().BeFalse();
        windowBuilt.Should().BeFalse();
        (await SyncJobsRepository.GetRecentAsync(ConnectionString, clubCode, null)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task RunSyncAsync_GelijktijdigeRunsVoorVerschillendeClubs_WordtGlobaalGeserialiseerd()
    {
        var clubA = TestClubCode();
        var clubB = TestClubCode();
        using var server = SportlinkFixtures.BuildServer(90000001, clubA);
        var teamsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTeams = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RespondWithJsonAsync("/teams", async _ =>
        {
            teamsEntered.TrySetResult();
            await releaseTeams.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return TeamsJson(clubA, 90001);
        });

        var first = RunAsync(clubA, server);
        try
        {
            await teamsEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var second = () => RunAsync(clubB, server);
            await second.Should().ThrowAsync<SyncAlreadyRunningException>();
            lock (server.Requests)
                server.Requests.Count(r => r.StartsWith("/teams", StringComparison.Ordinal)).Should().Be(1);
        }
        finally
        {
            releaseTeams.TrySetResult();
            try
            {
                await first;
            }
            finally
            {
                await CleanupAsync(clubA, clubB);
            }
        }
    }

    [PostgresFact]
    public async Task RunSyncAsync_MergeFout_BewaartTimestampEnGeeftLeaseVrij()
    {
        var clubCode = TestClubCode();
        using var server = SportlinkFixtures.BuildServer(90000002, clubCode);
        server.RespondWithJson("/teams", TeamsJson(clubCode, 90001));
        await MaakAppSettingsAsync(clubCode);
        var constraintAdded = false;
        try
        {
            await RunAsync(clubCode, server);
            var oldTimestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await SetTimestampAsync(clubCode, oldTimestamp);
            await ExecuteAsync($"ALTER TABLE his.teams DROP CONSTRAINT IF EXISTS {MergeFailureConstraint};");
            await ExecuteAsync($"ALTER TABLE his.teams ADD CONSTRAINT {MergeFailureConstraint} CHECK (teamcode <> 90002);");
            constraintAdded = true;

            server.RespondWithJson("/teams", TeamsJson(clubCode, 90002));
            var run = () => RunAsync(clubCode, server);
            await run.Should().ThrowAsync<PostgresException>();

            (await GetTimestampAsync(clubCode)).Should().Be(oldTimestamp);
            var lease = await PostgresSyncStagingLease.TryAcquireAsync(ConnectionString);
            lease.Should().NotBeNull("the failed merge must release the session advisory lock");
            await lease!.DisposeAsync();
        }
        finally
        {
            if (constraintAdded)
                await ExecuteAsync($"ALTER TABLE his.teams DROP CONSTRAINT IF EXISTS {MergeFailureConstraint};");
            await CleanupAsync(clubCode);
        }
    }

    private static string TestClubCode() => "t" + Guid.NewGuid().ToString("N")[..12];

    private static string TeamsJson(string clubCode, int teamCode) => $$"""
        [{"teamcode":{{teamCode}},"lokaleteamcode":1,"poulecode":7,"teamnaam":"{{clubCode}} JO13-1"}]
        """;

    private static Task RunAsync(string clubCode, SportlinkFixtureServer server) =>
        PostgresSyncPipeline.RunSyncAsync(0, 0, server.BaseUrl, "clientId=fixture-test", clubCode,
            ConnectionString, NullLogger.Instance, new DateOnly(2026, 9, 1));

    private static async Task MaakAppSettingsAsync(string clubCode)
    {
        await ExecuteAsync(
            "INSERT INTO public.appsettings (clubcode, syncenabled, lastsynctimestamp) VALUES (@club, true, '2024-01-01T00:00:00Z');",
            ("club", clubCode));
    }

    private static Task SetTimestampAsync(string clubCode, DateTime timestamp) =>
        ExecuteAsync("UPDATE public.appsettings SET lastsynctimestamp = @timestamp WHERE clubcode = @club;",
            ("timestamp", timestamp), ("club", clubCode));

    private static async Task<DateTime?> GetTimestampAsync(string clubCode)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT lastsynctimestamp FROM public.appsettings WHERE clubcode = @club;", connection);
        command.Parameters.AddWithValue("club", clubCode);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (DateTime)value;
    }

    private static async Task CleanupAsync(params string[] clubCodes)
    {
        foreach (var clubCode in clubCodes)
        {
            await ExecuteAsync(
                "DELETE FROM his.matchdetails WHERE clubcode = @club; " +
                "DELETE FROM his.matches WHERE clubcode = @club; " +
                "DELETE FROM his.teams WHERE clubcode = @club; " +
                "DELETE FROM public.appsettings WHERE clubcode = @club;", ("club", clubCode));
        }
    }

    private static async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
