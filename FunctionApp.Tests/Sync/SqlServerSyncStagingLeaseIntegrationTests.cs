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

    [SqlServerFact]
    public async Task RunSyncAsync_ComposieteTeamSleutels_ZijnOndubbelzinnigEnBewarenHistorischeSleutel()
    {
        var clubCode = TestClubCode();
        var priorSettings = await MaakAlleenTestclubSyncbaarAsync(clubCode);
        SystemUtilities.AppSettings.SetForTests("clubCode", clubCode);
        using var server = SportlinkFixtures.BuildServer(90000003, clubCode);
        server.RespondWithJson("/teams", TeamsJsonForCompositeKey(clubCode, includeCollision: false));

        try
        {
            await RunAsync(server);

            // Simulate a key persisted by the pre-fix encoding. The component match must still
            // update this row without rewriting its historical business key.
            await ExecuteAsync(
                "UPDATE [his].[teams] SET [bk_teams] = N'1234' " +
                "WHERE [ClubCode] = @club AND [teamcode] = 1 AND [lokaleteamcode] = 23 AND [poulecode] = 4;",
                ("@club", clubCode));

            server.RespondWithJson("/teams", TeamsJsonForCompositeKey(clubCode, includeCollision: true));
            await RunAsync(server);

            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [teamcode], [lokaleteamcode], [poulecode], [bk_teams] " +
                "FROM [his].[teams] WHERE [ClubCode] = @club;", connection);
            command.Parameters.AddWithValue("@club", clubCode);
            var keys = new Dictionary<(long TeamCode, long LocalCode, long? PouleCode), string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var pouleCode = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                    keys.Add((reader.GetInt64(0), reader.GetInt64(1), pouleCode), reader.GetString(3));
                }
            }

            keys.Should().HaveCount(4, "distinct tuples, including the historical one, remain separate");
            keys[(1, 23, 4)].Should().Be("1234", "existing stored business keys are not rewritten");
            keys[(12, 3, 4)].Should().Be($"12\u00013\u00014");
            keys[(30, 1, null)].Should().Be($"30\u00011\u0001", "NULL remains the empty component");
            keys[(30, 1, 0)].Should().Be($"30\u00011\u00010", "zero remains distinct from NULL");

            await using (var matchConnection = new SqlConnection(ConnectionString))
            {
                await matchConnection.OpenAsync();
                await using var matchCommand = new SqlCommand(
                    "SELECT [bk_matches] FROM [his].[matches] WHERE [ClubCode] = @club AND [wedstrijdcode] = 90000003;",
                    matchConnection);
                matchCommand.Parameters.AddWithValue("@club", clubCode);
                ((string?)await matchCommand.ExecuteScalarAsync()).Should().Be("90000003",
                    "single-column business keys retain their established format");
            }

            // A second identical pass proves the generated keys also match and do not duplicate.
            await RunAsync(server);
            (await CountTeamsAsync(clubCode)).Should().Be(4);
        }
        finally
        {
            await CleanupAsync(clubCode, priorSettings);
            SystemUtilities.AppSettings.ResetForTests();
        }
    }

    private static string TestClubCode() => "t" + Guid.NewGuid().ToString("N")[..12];

    private static string TeamsJson(string clubCode, int teamCode) => $$"""
        [{"teamcode":{{teamCode}},"lokaleteamcode":1,"poulecode":7,"teamnaam":"{{clubCode}} JO13-1"}]
        """;

    private static string TeamsJsonForCompositeKey(string clubCode, bool includeCollision)
    {
        if (!includeCollision)
        {
            return $$"""
                [{"teamcode":1,"lokaleteamcode":23,"poulecode":4,"teamnaam":"{{clubCode}} JO13-1"}]
                """;
        }

        return $$"""
                [
                  {"teamcode":1,"lokaleteamcode":23,"poulecode":4,"teamnaam":"{{clubCode}} JO13-1"},
                  {"teamcode":12,"lokaleteamcode":3,"poulecode":4,"teamnaam":"{{clubCode}} JO13-2"},
                  {"teamcode":30,"lokaleteamcode":1,"poulecode":null,"teamnaam":"{{clubCode}} JO13-3"},
                  {"teamcode":30,"lokaleteamcode":1,"poulecode":0,"teamnaam":"{{clubCode}} JO13-4"}
                ]
                """;
    }

    private static async Task<int> CountTeamsAsync(string clubCode)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [his].[teams] WHERE [ClubCode] = @club;", connection);
        command.Parameters.AddWithValue("@club", clubCode);
        return (int)(await command.ExecuteScalarAsync())!;
    }

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
