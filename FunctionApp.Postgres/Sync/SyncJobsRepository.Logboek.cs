using Npgsql;
using Planner.Shared.Sync;

namespace FunctionApp.Postgres.Sync;

internal static partial class SyncJobsRepository
{
    private const string JobColumns = "id,status,weekoffsetfrom,weekoffsetto,createdat,startedat,completedat,errormessage,attempt";

    internal static async Task<SyncJobCreation> GetOrCreateActiveAsync(
        string connectionString, Guid id, string clubCode, int from, int to)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1912, hashtext(@club))", connection, transaction))
        {
            gate.Parameters.AddWithValue("club", clubCode);
            await gate.ExecuteNonQueryAsync();
        }
        var active = await ReadActiveAsync(connection, transaction, clubCode);
        if (active is not null)
        {
            await transaction.CommitAsync();
            return new(active, false);
        }
        await using var insert = new NpgsqlCommand(
            "INSERT INTO public.syncjobs(id,clubcode,status,weekoffsetfrom,weekoffsetto) " +
            "VALUES (@id,@club,'pending',@from,@to) RETURNING " + JobColumns, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("club", clubCode);
        insert.Parameters.AddWithValue("from", from);
        insert.Parameters.AddWithValue("to", to);
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
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await ReadActiveAsync(connection, null, clubCode);
    }

    private static async Task<SyncJobStatus?> ReadActiveAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string clubCode)
    {
        await using var command = new NpgsqlCommand("SELECT " + JobColumns +
            " FROM public.syncjobs WHERE clubcode=@club AND (status='running' OR (status='pending' AND (attempt>0 OR createdat>@cutoff))) " +
            "ORDER BY createdat DESC LIMIT 1", connection, transaction);
        command.Parameters.AddWithValue("club", clubCode);
        command.Parameters.AddWithValue("cutoff", SyncJobRecoveryPolicy.StaleCutoff(DateTime.UtcNow));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? SyncJobStatus.FromReader(reader) : null;
    }

    internal static async Task<IReadOnlyList<SyncJobStatus>> GetRecentAsync(string connectionString, string clubCode, Guid? jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT " + JobColumns + " FROM public.syncjobs WHERE clubcode=@club AND (" +
            "id IN (SELECT id FROM public.syncjobs WHERE clubcode=@club ORDER BY createdat DESC LIMIT 10) " +
            "OR id=@id OR id IN (SELECT id FROM public.syncjobs WHERE clubcode=@club AND (status='running' OR (status='pending' AND (attempt>0 OR createdat>@cutoff))) ORDER BY createdat DESC LIMIT 1) " +
            "OR id IN (SELECT id FROM public.syncjobs WHERE clubcode=@club AND completedat IS NOT NULL ORDER BY completedat DESC LIMIT 1)) " +
            "ORDER BY createdat DESC", connection);
        command.Parameters.AddWithValue("club", clubCode);
        command.Parameters.AddWithValue("cutoff", SyncJobRecoveryPolicy.StaleCutoff(DateTime.UtcNow));
        command.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)jobId ?? DBNull.Value);
        var jobs = new List<SyncJobStatus>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) jobs.Add(SyncJobStatus.FromReader(reader));
        return jobs;
    }

    internal static async Task MarkEnqueueFailedAsync(string connectionString, Guid id, string clubCode)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE public.syncjobs SET status='failed',completedat=now()," +
            "errormessage='Synchronisatie kon niet worden ingepland. Probeer opnieuw.' " +
            "WHERE id=@id AND clubcode=@club AND status='pending' AND attempt=0", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("club", clubCode);
        command.Parameters.AddWithValue("cutoff", SyncJobRecoveryPolicy.StaleCutoff(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync();
    }
}
