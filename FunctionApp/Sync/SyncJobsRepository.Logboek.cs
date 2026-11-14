using Microsoft.Data.SqlClient;
using Planner.Shared.Sync;

namespace SportlinkFunction;

internal static partial class SyncJobsRepository
{
    private const string JobColumns = "Id,Status,WeekOffsetFrom,WeekOffsetTo,CreatedAt,StartedAt,CompletedAt,ErrorMessage,Attempt";

    internal static async Task<SyncJobCreation> GetOrCreateActiveAsync(
        string connectionString, Guid id, string clubCode, int from, int to)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using (var gate = new SqlCommand("DECLARE @result int; EXEC @result=sp_getapplock " +
            "@Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; " +
            "IF @result < 0 THROW 51000, 'Sync-start tijdelijk bezet.', 1;", connection, transaction))
        {
            gate.Parameters.AddWithValue("@resource", "sync-start");
            await gate.ExecuteNonQueryAsync();
        }
        var active = await ReadActiveAsync(connection, transaction, clubCode);
        if (active is not null)
        {
            await transaction.CommitAsync();
            return new(active, false);
        }
        await using var insert = new SqlCommand(
            "INSERT INTO dbo.SyncJobs(Id,ClubCode,Status,WeekOffsetFrom,WeekOffsetTo) " +
            "OUTPUT INSERTED.Id,INSERTED.Status,INSERTED.WeekOffsetFrom,INSERTED.WeekOffsetTo,INSERTED.CreatedAt," +
            "INSERTED.StartedAt,INSERTED.CompletedAt,INSERTED.ErrorMessage,INSERTED.Attempt " +
            "VALUES (@id,@club,'pending',@from,@to)", connection, transaction);
        insert.Parameters.AddWithValue("@id", id);
        insert.Parameters.AddWithValue("@club", clubCode);
        insert.Parameters.AddWithValue("@from", from);
        insert.Parameters.AddWithValue("@to", to);
        SyncJobStatus job;
        await using (var reader = await insert.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            job = SyncJobStatus.FromReader(reader);
        }
        await transaction.CommitAsync();
        return new(job, true);
    }

    internal static async Task<SyncJobStatus?> GetActiveAsync(string connectionString, string clubCode)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await ReadActiveAsync(connection, null, clubCode);
    }

    private static async Task<SyncJobStatus?> ReadActiveAsync(SqlConnection connection, SqlTransaction? transaction, string clubCode)
    {
        await using var command = new SqlCommand("SELECT TOP 1 " + JobColumns +
            " FROM dbo.SyncJobs WHERE ClubCode=@club AND (Status='running' OR (Status='pending' AND (Attempt>0 OR CreatedAt>@cutoff))) " +
            "ORDER BY CreatedAt DESC", connection, transaction);
        command.Parameters.AddWithValue("@club", clubCode);
        command.Parameters.AddWithValue("@cutoff", SyncJobRecoveryPolicy.StaleCutoff(DateTime.UtcNow));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? SyncJobStatus.FromReader(reader) : null;
    }

    internal static async Task<IReadOnlyList<SyncJobStatus>> GetRecentAsync(string connectionString, string clubCode, Guid? jobId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT " + JobColumns + " FROM dbo.SyncJobs WHERE ClubCode=@club AND (" +
            "Id IN (SELECT TOP 10 Id FROM dbo.SyncJobs WHERE ClubCode=@club ORDER BY CreatedAt DESC) " +
            "OR Id=@id OR Id IN (SELECT TOP 1 Id FROM dbo.SyncJobs WHERE ClubCode=@club AND (Status='running' OR (Status='pending' AND (Attempt>0 OR CreatedAt>@cutoff))) ORDER BY CreatedAt DESC) " +
            "OR Id IN (SELECT TOP 1 Id FROM dbo.SyncJobs WHERE ClubCode=@club AND CompletedAt IS NOT NULL ORDER BY CompletedAt DESC)) " +
            "ORDER BY CreatedAt DESC", connection);
        command.Parameters.AddWithValue("@club", clubCode);
        command.Parameters.AddWithValue("@cutoff", SyncJobRecoveryPolicy.StaleCutoff(DateTime.UtcNow));
        command.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = (object?)jobId ?? DBNull.Value;
        var jobs = new List<SyncJobStatus>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) jobs.Add(SyncJobStatus.FromReader(reader));
        return jobs;
    }

    internal static async Task MarkEnqueueFailedAsync(string connectionString, Guid id, string clubCode)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("UPDATE dbo.SyncJobs SET Status='failed',CompletedAt=GETUTCDATE()," +
            "ErrorMessage='Synchronisatie kon niet worden ingepland. Probeer opnieuw.' " +
            "WHERE Id=@id AND ClubCode=@club AND Status='pending' AND Attempt=0", connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@club", clubCode);
        command.Parameters.AddWithValue("@cutoff", SyncJobRecoveryPolicy.StaleCutoff(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync();
    }
}
