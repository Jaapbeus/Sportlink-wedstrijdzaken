using System.Data;
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

    internal static Task<int> CleanupAllAsync(DateTime utcNow)
        => CleanupAllAsync(SystemUtilities.DatabaseConfig.ConnectionString, utcNow);

    internal static async Task UpsertAsync(string connectionString, string clubCode, SyncLogEntry entry)
    {
        SyncLogPersistenceValues.ValidateUpsert(clubCode, entry);
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
            if (!SyncLogPersistenceValues.IsCurrentAttempt(currentAttempt, entry.Attempt))
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
        return await SyncLogPersistenceValues.QueryJobsAsync(clubCode, jobIds, async ids =>
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var parameters = ids.Select((_, index) => "@JobId" + index).ToArray();
            using var command = new SqlCommand(
                "SELECT [JobId], [Attempt], [Phase], [StartedAt], [CompletedAt], [Status], [Fetched], [Added], [Updated], [Failed], [Progress], [Total], [ReasonCode] " +
                "FROM [dbo].[SyncJobPhaseLog] WHERE [ClubCode] = @ClubCode AND [JobId] IN (" + string.Join(", ", parameters) + ") " +
                "ORDER BY [StartedAt], [Phase], [Attempt]",
                connection);
            command.Parameters.AddWithValue("@ClubCode", clubCode);
            for (var i = 0; i < ids.Length; i++)
                command.Parameters.AddWithValue(parameters[i], ids[i]);

            using var reader = await command.ExecuteReaderAsync();
            return await SyncLogPersistenceValues.ReadAllAsync(reader, specifyUtc: true);
        });
    }

    internal static async Task<int> CleanupAsync(string connectionString, string clubCode, DateTime utcNow)
    {
        var cutoff = SyncLogPersistenceValues.RetentionCutoff(clubCode, utcNow);
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            "DELETE FROM [dbo].[SyncJobPhaseLog] WHERE [ClubCode] = @ClubCode AND [StartedAt] < @Cutoff",
            connection);
        command.Parameters.AddWithValue("@ClubCode", clubCode);
        command.Parameters.Add("@Cutoff", SqlDbType.DateTime2).Value = cutoff;
        return await command.ExecuteNonQueryAsync();
    }

    internal static async Task<int> CleanupAllAsync(string connectionString, DateTime utcNow)
    {
        var cutoff = SyncLogPersistenceValues.RetentionCutoff(utcNow);
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var command = new SqlCommand(
            "DELETE FROM [dbo].[SyncJobPhaseLog] WHERE [StartedAt] < @Cutoff", connection);
        command.Parameters.Add("@Cutoff", SqlDbType.DateTime2).Value = cutoff;
        return await command.ExecuteNonQueryAsync();
    }

    private static void AddParameters(SqlCommand command, string clubCode, SyncLogEntry entry)
    {
        foreach (var (name, value) in SyncLogPersistenceValues.Parameters(clubCode, entry))
        {
            if (name is "startedat" or "completedat")
                command.Parameters.Add("@" + name, SqlDbType.DateTime2).Value = value;
            else
                command.Parameters.AddWithValue("@" + name, value);
        }
    }
}
