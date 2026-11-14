using FunctionApp.Tests.Feedback;
using Microsoft.Data.SqlClient;
using Planner.Shared.Sync;
using SportlinkFunction;
using Xunit;

namespace FunctionApp.Tests.Sync;

public sealed class SqlServerSyncJobsRepositoryIntegrationTests_Logboek
{
    private const string Club = "testclub-synclog-a";
    private const string OtherClub = "testclub-synclog-b";
    private static readonly DateTime Now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc).AddTicks(123450);
    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvVar)!;

    [SqlServerFact]
    public async Task UpsertScopesByClubAttemptAndImmutableTerminalPhase()
    {
        var id = Guid.NewGuid();
        using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        try
        {
            await InsertJobAsync(connection, id, Club);
            var running = Entry(id, Now, null, SyncLogPolicy.StatusRunning);
            await SyncLogRepository.UpsertAsync(ConnectionString, Club, running);
            Assert.Empty(await SyncLogRepository.GetForJobsAsync(ConnectionString, OtherClub, [id]));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SyncLogRepository.UpsertAsync(ConnectionString, OtherClub, running));
            await SyncLogRepository.UpsertAsync(ConnectionString, Club,
                Entry(id, Now, Now.AddMinutes(1), SyncLogPolicy.StatusSucceeded) with { Fetched = 8 });
            await SyncLogRepository.UpsertAsync(ConnectionString, Club,
                Entry(id, Now, Now.AddMinutes(2), SyncLogPolicy.StatusFailed));

            var result = Assert.Single(await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [id, id]));
            Assert.Equal(SyncLogPolicy.StatusSucceeded, result.Status);
            Assert.Equal(8, result.Fetched);
            Assert.Equal(Now, result.StartedAt);
            Assert.Equal(Now.AddMinutes(1), result.CompletedAt);

            using (var update = new SqlCommand("UPDATE dbo.SyncJobs SET Attempt = 2 WHERE Id = @id", connection))
            {
                update.Parameters.AddWithValue("@id", id);
                await update.ExecuteNonQueryAsync();
            }
            await SyncLogRepository.UpsertAsync(ConnectionString, Club,
                Entry(id, Now.AddMinutes(3), null, SyncLogPolicy.StatusRunning));
            Assert.Single(await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [id]));
        }
        finally
        {
            await DeleteJobAsync(connection, id);
        }
    }

    [SqlServerFact]
    public async Task CleanupUsesThirtyDayBoundaryForOneClubOnly()
    {
        var old = Guid.NewGuid();
        var current = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        try
        {
            await InsertJobAsync(connection, old, Club);
            await InsertJobAsync(connection, current, Club);
            await InsertJobAsync(connection, other, OtherClub);
            await SyncLogRepository.UpsertAsync(ConnectionString, Club,
                Entry(old, Now.AddDays(-SyncLogPolicy.RetentionDays).AddTicks(-10), null, SyncLogPolicy.StatusRunning));
            await SyncLogRepository.UpsertAsync(ConnectionString, Club,
                Entry(current, Now.AddDays(-SyncLogPolicy.RetentionDays), null, SyncLogPolicy.StatusRunning));
            await SyncLogRepository.UpsertAsync(ConnectionString, OtherClub,
                Entry(other, Now.AddDays(-SyncLogPolicy.RetentionDays - 1), null, SyncLogPolicy.StatusRunning));

            Assert.Equal(1, await SyncLogRepository.CleanupAsync(ConnectionString, Club, Now));
            Assert.Empty(await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [old]));
            Assert.Single(await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [current]));
            Assert.Single(await SyncLogRepository.GetForJobsAsync(ConnectionString, OtherClub, [other]));

        }
        finally
        {
            await DeleteJobAsync(connection, old);
            await DeleteJobAsync(connection, current);
            await DeleteJobAsync(connection, other);
        }
    }

    [SqlServerFact]
    public async Task CleanupAllUsesExactBoundaryForClubsWithoutActiveConfiguration()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        try
        {
            // Deze jobs hebben geen actieve clubconfiguratie; retentie mag daarvan niet afhangen.
            for (var i = 0; i < ids.Length; i++)
            {
                var club = i < 2 ? Club : OtherClub;
                await InsertJobAsync(connection, ids[i], club);
                var startedAt = Now.AddDays(-SyncLogPolicy.RetentionDays).AddTicks(i % 2 == 0 ? -10 : 0);
                await SyncLogRepository.UpsertAsync(ConnectionString, club,
                    Entry(ids[i], startedAt, null, SyncLogPolicy.StatusRunning));
            }

            Assert.Equal(2, await SyncLogRepository.CleanupAllAsync(ConnectionString, Now));
            Assert.Empty(await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [ids[0]]));
            Assert.Single(await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [ids[1]]));
            Assert.Empty(await SyncLogRepository.GetForJobsAsync(ConnectionString, OtherClub, [ids[2]]));
            Assert.Single(await SyncLogRepository.GetForJobsAsync(ConnectionString, OtherClub, [ids[3]]));
            Assert.Equal(0, await SyncLogRepository.CleanupAllAsync(ConnectionString, Now));
            await Assert.ThrowsAsync<ArgumentException>(() => SyncLogRepository.CleanupAllAsync(
                ConnectionString, DateTime.SpecifyKind(Now, DateTimeKind.Local)));
        }
        finally
        {
            foreach (var id in ids)
                await DeleteJobAsync(connection, id);
        }
    }

    private static SyncLogEntry Entry(Guid id, DateTime start, DateTime? end, string status) =>
        new(id, 1, SyncLogPolicy.PhaseTeams, start, end, status,
            null, null, null, null, null, null, null);

    private static async Task InsertJobAsync(SqlConnection connection, Guid id, string club)
    {
        using var command = new SqlCommand(
            "INSERT INTO dbo.SyncJobs (Id, ClubCode, WeekOffsetFrom, WeekOffsetTo, Attempt) VALUES (@id, @club, 0, 0, 1)",
            connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@club", club);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteJobAsync(SqlConnection connection, Guid id)
    {
        using var command = new SqlCommand("DELETE FROM dbo.SyncJobs WHERE Id = @id", connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }
}
