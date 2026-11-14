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

    internal static async Task UpsertAsync(string connectionString, string clubCode, SyncLogEntry entry)
    {
        ValidateArguments(clubCode, entry);
        SyncLogPolicy.Validate(entry);
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
            if (currentAttempt is null)
                throw new InvalidOperationException("Sync-job niet gevonden.");
            if (Convert.ToInt32(currentAttempt) != entry.Attempt)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(clubCode);
        ArgumentNullException.ThrowIfNull(jobIds);
        if (jobIds.Count == 0)
            return Array.Empty<SyncLogEntry>();
        if (jobIds.Count > 100)
            throw new ArgumentOutOfRangeException(nameof(jobIds), "Maximaal 100 sync-jobs per logboekvraag.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT jobid, attempt, phase, startedat, completedat, status, fetched, added, updated, failed, progress, total, reasoncode " +
            "FROM public.syncjobphaselog WHERE clubcode = @clubcode AND jobid = ANY(@jobids) " +
            "ORDER BY startedat, phase, attempt",
            connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        command.Parameters.AddWithValue("jobids", jobIds.Distinct().ToArray());
        var results = new List<SyncLogEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new SyncLogEntry(
                reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.GetString(5),
                NullableInt(reader, 6), NullableInt(reader, 7), NullableInt(reader, 8), NullableInt(reader, 9),
                NullableInt(reader, 10), NullableInt(reader, 11), reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return results;
    }

    internal static async Task<int> CleanupAsync(string connectionString, string clubCode, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clubCode);
        SyncLogPolicy.RequireUtc(utcNow, nameof(utcNow));
        var cutoff = utcNow.AddDays(-SyncLogPolicy.RetentionDays);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM public.syncjobphaselog WHERE clubcode = @clubcode AND startedat < @cutoff",
            connection);
        command.Parameters.AddWithValue("clubcode", clubCode);
        command.Parameters.AddWithValue("cutoff", cutoff);
        return await command.ExecuteNonQueryAsync();
    }

    private static void ValidateArguments(string clubCode, SyncLogEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clubCode);
        ArgumentNullException.ThrowIfNull(entry);
    }

    private static void AddParameters(NpgsqlCommand command, string clubCode, SyncLogEntry entry)
    {
        command.Parameters.AddWithValue("jobid", entry.JobId);
        command.Parameters.AddWithValue("clubcode", clubCode);
        command.Parameters.AddWithValue("attempt", entry.Attempt);
        command.Parameters.AddWithValue("phase", entry.Phase);
        command.Parameters.AddWithValue("startedat", entry.StartedAt);
        command.Parameters.AddWithValue("completedat", (object?)entry.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("status", entry.Status);
        command.Parameters.AddWithValue("fetched", (object?)entry.Fetched ?? DBNull.Value);
        command.Parameters.AddWithValue("added", (object?)entry.Added ?? DBNull.Value);
        command.Parameters.AddWithValue("updated", (object?)entry.Updated ?? DBNull.Value);
        command.Parameters.AddWithValue("failed", (object?)entry.Failed ?? DBNull.Value);
        command.Parameters.AddWithValue("progress", (object?)entry.Progress ?? DBNull.Value);
        command.Parameters.AddWithValue("total", (object?)entry.Total ?? DBNull.Value);
        command.Parameters.AddWithValue("reasoncode", (object?)entry.ReasonCode ?? DBNull.Value);
    }

    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
