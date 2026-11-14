using Database.Postgres.Tests;
using FunctionApp.Postgres.Sync;
using Npgsql;
using Planner.Shared.Sync;
using Xunit;

namespace FunctionApp.Postgres.Tests.Sync;

public sealed class SyncLogRepositoryIntegrationTests
{
    private const string Club = "testclub-synclog-a";
    private const string OtherClub = "testclub-synclog-b";
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException("Postgres-testverbinding ontbreekt.");

    [PostgresFact]
    public async Task UpsertScopesByClubAttemptAndImmutableTerminalPhase()
    {
        var job = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        try
        {
            await InsertJobAsync(connection, job);
            var running = Entry(job, Now, null, SyncLogPolicy.StatusRunning);
            await SyncLogRepository.UpsertAsync(ConnectionString, Club, running);
            Assert.Empty(await SyncLogRepository.GetForJobsAsync(ConnectionString, OtherClub, [job]));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SyncLogRepository.UpsertAsync(ConnectionString, OtherClub, running));

            var terminal = Entry(job, Now, Now.AddMinutes(1), SyncLogPolicy.StatusSucceeded) with
            {
                Fetched = 8, ReasonCode = null
            };
            await SyncLogRepository.UpsertAsync(ConnectionString, Club, terminal);
            await SyncLogRepository.UpsertAsync(ConnectionString, Club,
                terminal with { CompletedAt = Now.AddMinutes(2), Status = SyncLogPolicy.StatusFailed });
            var rows = await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [job]);
            var result = Assert.Single(rows);
            Assert.Equal(SyncLogPolicy.StatusSucceeded, result.Status);
            Assert.Equal(8, result.Fetched);
            Assert.Equal(Now.AddMinutes(1), result.CompletedAt);

            await using (var command = new NpgsqlCommand("UPDATE public.syncjobs SET attempt = 2 WHERE id = @id", connection))
            {
                command.Parameters.AddWithValue("id", job);
                await command.ExecuteNonQueryAsync();
            }
            await SyncLogRepository.UpsertAsync(ConnectionString, Club,
                Entry(job, Now.AddMinutes(3), null, SyncLogPolicy.StatusRunning));
            Assert.Single(await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [job]));
        }
        finally
        {
            await DeleteJobAsync(connection, job);
        }
    }

    [PostgresFact]
    public async Task CleanupUsesThirtyDayBoundaryForOneClubOnly()
    {
        var oldJob = Guid.NewGuid();
        var currentJob = Guid.NewGuid();
        var otherJob = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        try
        {
            await InsertJobAsync(connection, oldJob);
            await InsertJobAsync(connection, currentJob);
            await InsertJobAsync(connection, otherJob, OtherClub);
            await SyncLogRepository.UpsertAsync(ConnectionString, Club,
                Entry(oldJob, Now.AddDays(-SyncLogPolicy.RetentionDays).AddTicks(-10), null, SyncLogPolicy.StatusRunning));
            await SyncLogRepository.UpsertAsync(ConnectionString, Club,
                Entry(currentJob, Now.AddDays(-SyncLogPolicy.RetentionDays), null, SyncLogPolicy.StatusRunning));
            await SyncLogRepository.UpsertAsync(ConnectionString, OtherClub,
                Entry(otherJob, Now.AddDays(-SyncLogPolicy.RetentionDays).AddDays(-1), null, SyncLogPolicy.StatusRunning));

            Assert.Equal(1, await SyncLogRepository.CleanupAsync(ConnectionString, Club, Now));
            Assert.Empty(await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [oldJob]));
            Assert.Single(await SyncLogRepository.GetForJobsAsync(ConnectionString, Club, [currentJob]));
            Assert.Single(await SyncLogRepository.GetForJobsAsync(ConnectionString, OtherClub, [otherJob]));
        }
        finally
        {
            await DeleteJobAsync(connection, oldJob);
            await DeleteJobAsync(connection, currentJob);
            await DeleteJobAsync(connection, otherJob);
        }
    }

    private static SyncLogEntry Entry(Guid job, DateTime start, DateTime? end, string status) =>
        new(job, 1, SyncLogPolicy.PhaseTeams, start, end, status,
            null, null, null, null, null, null, null);

    private static async Task InsertJobAsync(NpgsqlConnection connection, Guid id, string club = Club)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO public.syncjobs (id, clubcode, status, weekoffsetfrom, weekoffsetto, attempt) " +
            "VALUES (@id, @club, 'running', 0, 0, 1)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("club", club);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteJobAsync(NpgsqlConnection connection, Guid id)
    {
        await using var command = new NpgsqlCommand("DELETE FROM public.syncjobs WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
}
