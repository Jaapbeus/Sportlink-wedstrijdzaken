using AwesomeAssertions;
using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Sync;
using SportlinkFunction;
using Xunit;

namespace FunctionApp.Tests.Sync;

public sealed class SqlServerSyncStagingLeaseIntegrationTests
{
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;

    [SqlServerFact]
    public async Task RunSyncAsync_GelijktijdigeRun_WordtVoorStagingGeweigerd()
    {
        var clubCode = TestClubCode();
        var priorSettings = await MaakAlleenTestclubSyncbaarAsync(clubCode);
        SystemUtilities.AppSettings.SetForTests("clubCode", clubCode);
        using var server = new SportlinkFixtureServer();
        var teamsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTeams = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RespondWithJsonAsync("/teams", async _ =>
        {
            teamsEntered.TrySetResult();
            await releaseTeams.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return TeamsJson(clubCode, 90001);
        });
        server.RespondWithJson("/programma", "[]");
        server.RespondWithJson("/uitslagen", "[]");

        var first = RunAsync(server);
        try
        {
            await teamsEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var second = () => RunAsync(server);
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
                await CleanupAsync(clubCode, priorSettings);
                SystemUtilities.AppSettings.ResetForTests();
            }
        }
    }

    [SqlServerFact]
    public async Task RunSyncAsync_MergeFout_GeenTimestampUpdate_EnLeaseVrijgegeven()
    {
        var clubCode = TestClubCode();
        var priorSettings = await MaakAlleenTestclubSyncbaarAsync(clubCode);
        SystemUtilities.AppSettings.SetForTests("clubCode", clubCode);
        using var server = new SportlinkFixtureServer();
        server.RespondWithJson("/teams", TeamsJson(clubCode, 90001));
        server.RespondWithJson("/programma", "[]");
        server.RespondWithJson("/uitslagen", "[]");
        var constraintAdded = false;
        try
        {
            await RunAsync(server);
            var oldTimestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await SetTimestampAsync(clubCode, oldTimestamp);
            await ExecuteAsync(
                "ALTER TABLE [his].[teams] ADD CONSTRAINT [CK_1818_sync_merge_failure] CHECK ([teamcode] <> 90002);");
            constraintAdded = true;

            server.RespondWithJson("/teams", TeamsJson(clubCode, 90002));
            var run = () => RunAsync(server);
            await run.Should().ThrowAsync<SyncMergeFailedException>();

            (await GetTimestampAsync(clubCode)).Should().Be(oldTimestamp);
            var lease = await SqlServerSyncStagingLease.TryAcquireAsync();
            lease.Should().NotBeNull("the failed merge must release the session lock");
            await lease!.DisposeAsync();
        }
        finally
        {
            if (constraintAdded)
                await ExecuteAsync("ALTER TABLE [his].[teams] DROP CONSTRAINT [CK_1818_sync_merge_failure];");
            await CleanupAsync(clubCode, priorSettings);
            SystemUtilities.AppSettings.ResetForTests();
        }
    }

    private static string TestClubCode() => "t" + Guid.NewGuid().ToString("N")[..12];

    private static string TeamsJson(string clubCode, int teamCode) => $$"""
        [{"teamcode":{{teamCode}},"lokaleteamcode":1,"poulecode":7,"teamnaam":"{{clubCode}} JO13-1"}]
        """;

    private static Task RunAsync(SportlinkFixtureServer server) =>
        SportlinkSyncPipeline.RunSyncAsync(0, 0, server.BaseUrl, "clientId=fixture-test", NullLogger.Instance);

    private static async Task<List<(string ClubCode, bool SyncEnabled)>> MaakAlleenTestclubSyncbaarAsync(string clubCode)
    {
        var prior = new List<(string, bool)>();
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var select = new SqlCommand("SELECT [ClubCode], [SyncEnabled] FROM [dbo].[AppSettings];", connection))
        await using (var reader = await select.ExecuteReaderAsync())
            while (await reader.ReadAsync()) prior.Add((reader.GetString(0), reader.GetBoolean(1)));
        await using (var disable = new SqlCommand("UPDATE [dbo].[AppSettings] SET [SyncEnabled] = 0;", connection))
            await disable.ExecuteNonQueryAsync();
        await using var insert = new SqlCommand(
            "INSERT INTO [dbo].[AppSettings] ([ClubName], [ClubCode], [SportlinkApiUrl], [SportlinkClientId], [SeasonStartMonth], [SyncEnabled], [LastSyncTimestamp]) " +
            "VALUES (@name, @club, 'http://127.0.0.1', 'fixture', 7, 1, '2024-01-01T00:00:00Z');", connection);
        insert.Parameters.AddWithValue("@name", "Jan de Vries");
        insert.Parameters.AddWithValue("@club", clubCode);
        await insert.ExecuteNonQueryAsync();
        return prior;
    }

    private static async Task SetTimestampAsync(string clubCode, DateTime timestamp) =>
        await ExecuteAsync("UPDATE [dbo].[AppSettings] SET [LastSyncTimestamp] = @timestamp WHERE [ClubCode] = @club;",
            ("@timestamp", timestamp), ("@club", clubCode));

    private static async Task<DateTime?> GetTimestampAsync(string clubCode)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT [LastSyncTimestamp] FROM [dbo].[AppSettings] WHERE [ClubCode] = @club;", connection);
        command.Parameters.AddWithValue("@club", clubCode);
        return (DateTime?)await command.ExecuteScalarAsync();
    }

    private static async Task CleanupAsync(string clubCode, List<(string ClubCode, bool SyncEnabled)> prior)
    {
        await ExecuteAsync(
            "IF OBJECT_ID('[his].[matchdetails]') IS NOT NULL DELETE FROM [his].[matchdetails] WHERE [ClubCode] = @club; " +
            "IF OBJECT_ID('[his].[matches]') IS NOT NULL DELETE FROM [his].[matches] WHERE [ClubCode] = @club; " +
            "IF OBJECT_ID('[his].[teams]') IS NOT NULL DELETE FROM [his].[teams] WHERE [ClubCode] = @club; " +
            "DELETE FROM [dbo].[AppSettings] WHERE [ClubCode] = @club;", ("@club", clubCode));
        foreach (var (existingClub, enabled) in prior)
            await ExecuteAsync("UPDATE [dbo].[AppSettings] SET [SyncEnabled] = @enabled WHERE [ClubCode] = @club;",
                ("@enabled", enabled), ("@club", existingClub));
    }

    private static async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
