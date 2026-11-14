using Microsoft.Data.SqlClient;
using Planner.Shared.Sync;

namespace SportlinkFunction;

/// <summary>SQL Server-persistence voor het privacyveilige jobfase-logboek (#1912).</summary>
internal static class SyncLogRepository
{
    internal static Task UpsertAsync(string clubCode, SyncLogEntry entry)
        => UpsertAsync(SystemUtilities.DatabaseConfig.ConnectionString, clubCode, entry);

    internal static Task<IReadOnlyList<SyncLogEntry>> GetForJobsAsync(string clubCode, IReadOnlyCollection<Guid> jobIds)
        => GetForJobsAsync(SystemUtilities.DatabaseConfig.ConnectionString, clubCode, jobIds);

    internal static Task<int> CleanupAsync(string clubCode, DateTime utcNow)
        => CleanupAsync(SystemUtilities.DatabaseConfig.ConnectionString, clubCode, utcNow);

    internal static async Task UpsertAsync(string connectionString, string clubCode, SyncLogEntry entry)
    {
        ValidateArguments(clubCode, entry);
        SyncLogPolicy.Validate(entry);
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        using (var ownerCommand = new SqlCommand(
                   "SELECT [Attempt] FROM [dbo].[SyncJobs] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @JobId AND [ClubCode] = @ClubCode",
                   connection, transaction))
        {
            ownerCommand.Parameters.AddWithValue("@JobId", entry.JobId);
            ownerCommand.Parameters.AddWithValue("@ClubCode", clubCode);
            var currentAttempt = await ownerCommand.ExecuteScalarAsync();
            if (currentAttempt is null)
                throw new InvalidOperationException("Sync-job niet gevonden.");
            if (Convert.ToInt32(currentAttempt) != entry.Attempt)
            {
                await transaction.CommitAsync();
                return;
            }
        }

        using (var update = new SqlCommand(
                   "UPDATE [dbo].[SyncJobPhaseLog] SET [StartedAt] = @StartedAt, [CompletedAt] = @CompletedAt, [Status] = @Status, " +
                   "[Fetched] = @Fetched, [Added] = @Added, [Updated] = @Updated, [Failed] = @Failed, [Progress] = @Progress, [Total] = @Total, [ReasonCode] = @ReasonCode " +
                   "WHERE [JobId] = @JobId AND [Attempt] = @Attempt AND [Phase] = @Phase " +
                   "AND [CompletedAt] IS NULL",
                   connection, transaction))
        {
            AddParameters(update, clubCode, entry);
            if (await update.ExecuteNonQueryAsync() == 0)
            {
                using var insert = new SqlCommand(
                    "INSERT INTO [dbo].[SyncJobPhaseLog] ([JobId], [ClubCode], [Attempt], [Phase], [StartedAt], [CompletedAt], [Status], [Fetched], [Added], [Updated], [Failed], [Progress], [Total], [ReasonCode]) " +
                    "SELECT @JobId, @ClubCode, @Attempt, @Phase, @StartedAt, @CompletedAt, @Status, @Fetched, @Added, @Updated, @Failed, @Progress, @Total, @ReasonCode " +
                    "WHERE NOT EXISTS (SELECT 1 FROM [dbo].[SyncJobPhaseLog] WHERE [JobId] = @JobId AND [Attempt] = @Attempt AND [Phase] = @Phase)",
                    connection, transaction);
                AddParameters(insert, clubCode, entry);
                await insert.ExecuteNonQueryAsync();
            }
        }

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

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var ids = jobIds.Distinct().ToArray();
        var parameters = ids.Select((_, index) => "@JobId" + index).ToArray();
        using var command = new SqlCommand(
            "SELECT [JobId], [Attempt], [Phase], [StartedAt], [CompletedAt], [Status], [Fetched], [Added], [Updated], [Failed], [Progress], [Total], [ReasonCode] " +
            "FROM [dbo].[SyncJobPhaseLog] WHERE [ClubCode] = @ClubCode AND [JobId] IN (" + string.Join(", ", parameters) + ") " +
            "ORDER BY [StartedAt], [Phase], [Attempt]",
            connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        for (var i = 0; i < ids.Length; i++)
            command.Parameters.AddWithValue(parameters[i], ids[i]);

        var results = new List<SyncLogEntry>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new SyncLogEntry(
                reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), Utc(reader.GetDateTime(3)),
                reader.IsDBNull(4) ? null : Utc(reader.GetDateTime(4)), reader.GetString(5),
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
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            "DELETE FROM [dbo].[SyncJobPhaseLog] WHERE [ClubCode] = @ClubCode AND [StartedAt] < @Cutoff",
            connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.AddWithValue("@Cutoff", cutoff);
        return await command.ExecuteNonQueryAsync();
    }

    private static void ValidateArguments(string clubCode, SyncLogEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clubCode);
        ArgumentNullException.ThrowIfNull(entry);
    }

    private static void AddParameters(SqlCommand command, string clubCode, SyncLogEntry entry)
    {
        command.Parameters.AddWithValue("@JobId", entry.JobId);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.AddWithValue("@Attempt", entry.Attempt);
        command.Parameters.AddWithValue("@Phase", entry.Phase);
        command.Parameters.AddWithValue("@StartedAt", entry.StartedAt);
        command.Parameters.AddWithValue("@CompletedAt", (object?)entry.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", entry.Status);
        command.Parameters.AddWithValue("@Fetched", (object?)entry.Fetched ?? DBNull.Value);
        command.Parameters.AddWithValue("@Added", (object?)entry.Added ?? DBNull.Value);
        command.Parameters.AddWithValue("@Updated", (object?)entry.Updated ?? DBNull.Value);
        command.Parameters.AddWithValue("@Failed", (object?)entry.Failed ?? DBNull.Value);
        command.Parameters.AddWithValue("@Progress", (object?)entry.Progress ?? DBNull.Value);
        command.Parameters.AddWithValue("@Total", (object?)entry.Total ?? DBNull.Value);
        command.Parameters.AddWithValue("@ReasonCode", (object?)entry.ReasonCode ?? DBNull.Value);
    }

    private static int? NullableInt(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
