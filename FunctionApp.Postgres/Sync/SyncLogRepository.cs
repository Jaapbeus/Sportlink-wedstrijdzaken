using Npgsql;
using Planner.Shared.Sync;

namespace FunctionApp.Postgres.Sync;

/// <summary>Postgres-persistence voor het privacyveilige jobfase-logboek (#1912).</summary>
internal static class SyncLogRepository
{
    internal static Task UpsertAsync(string clubCode, SyncLogEntry entry)
        => UpsertAsync(PostgresDatabaseConfig.ConnectionString, clubCode, entry);

    internal static Task<IReadOnlyList<SyncLogEntry>> GetForJobsAsync(string clubCode, IReadOnlyCollection<Guid> jobIds)
        => GetForJobsAsync(PostgresDatabaseConfig.ConnectionString, clubCode, jobIds);

    internal static Task<int> CleanupAsync(string clubCode, DateTime utcNow)
        => CleanupAsync(PostgresDatabaseConfig.ConnectionString, clubCode, utcNow);

    internal static Task<int> CleanupAllAsync(DateTime utcNow)
        => CleanupAllAsync(PostgresDatabaseConfig.ConnectionString, utcNow);

    internal static async Task UpsertAsync(string connectionString, string clubCode, SyncLogEntry entry)
    {
        SyncLogPersistenceValues.ValidateUpsert(clubCode, entry);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var ownerCommand = new NpgsqlCommand(
                         "SELECT attempt FROM public.syncjobs WHERE id = @jobid AND clubcode = @clubcode FOR UPDATE",
                         connection, transaction))
        {
            ownerCommand.Parameters.AddWithValue("jobid", entry.JobId);
            ownerCommand.Parameters.AddWithValue("clubcode", clubCode);
            var currentAttempt = await ownerCommand.ExecuteScalarAsync();
            if (!SyncLogPersistenceValues.IsCurrentAttempt(currentAttempt, entry.Attempt))
            {
                await transaction.CommitAsync();
                return;
            }
        }

        await using var command = new NpgsqlCommand(
            "INSERT INTO public.syncjobphaselog " +
            "(jobid, clubcode, attempt, phase, startedat, completedat, status, fetched, added, updated, failed, progress, total, reasoncode) " +
            "VALUES (@jobid, @clubcode, @attempt, @phase, @startedat, @completedat, @status, @fetched, @added, @updated, @failed, @progress, @total, @reasoncode) " +
            "ON CONFLICT (jobid, attempt, phase) DO UPDATE SET " +
            "startedat = EXCLUDED.startedat, completedat = EXCLUDED.completedat, status = EXCLUDED.status, " +
            "fetched = EXCLUDED.fetched, added = EXCLUDED.added, updated = EXCLUDED.updated, failed = EXCLUDED.failed, " +
            "progress = EXCLUDED.progress, total = EXCLUDED.total, reasoncode = EXCLUDED.reasoncode " +
            "WHERE public.syncjobphaselog.completedat IS NULL",
            connection, transaction);
        AddParameters(command, clubCode, entry);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    internal static async Task<IReadOnlyList<SyncLogEntry>> GetForJobsAsync(
        string connectionString, string clubCode, IReadOnlyCollection<Guid> jobIds)
    {
        return await SyncLogPersistenceValues.QueryJobsAsync(clubCode, jobIds, async ids =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT jobid, attempt, phase, startedat, completedat, status, fetched, added, updated, failed, progress, total, reasoncode " +
                "FROM public.syncjobphaselog WHERE clubcode = @clubcode AND jobid = ANY(@jobids) " +
                "ORDER BY startedat, phase, attempt",
                connection);
            command.Parameters.AddWithValue("clubcode", clubCode);
            command.Parameters.AddWithValue("jobids", ids);
            await using var reader = await command.ExecuteReaderAsync();
            return await SyncLogPersistenceValues.ReadAllAsync(reader, specifyUtc: false);
        });
    }

    internal static async Task<int> CleanupAsync(string connectionString, string clubCode, DateTime utcNow)
    {
        var cutoff = SyncLogPersistenceValues.RetentionCutoff(clubCode, utcNow);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM public.syncjobphaselog WHERE clubcode = @clubcode AND startedat < @cutoff",
            connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        command.Parameters.AddWithValue("cutoff", cutoff);
        return await command.ExecuteNonQueryAsync();
    }

    internal static async Task<int> CleanupAllAsync(string connectionString, DateTime utcNow)
    {
        var cutoff = SyncLogPersistenceValues.RetentionCutoff(utcNow);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM public.syncjobphaselog WHERE startedat < @cutoff", connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        return await command.ExecuteNonQueryAsync();
    }

    private static void AddParameters(NpgsqlCommand command, string clubCode, SyncLogEntry entry)
    {
        foreach (var (name, value) in SyncLogPersistenceValues.Parameters(clubCode, entry))
            command.Parameters.AddWithValue(name, value);
    }
}
