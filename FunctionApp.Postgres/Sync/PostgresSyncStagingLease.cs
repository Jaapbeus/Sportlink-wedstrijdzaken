using Npgsql;

namespace FunctionApp.Postgres.Sync;

/// <summary>Holds one non-pooled PostgreSQL session advisory lock for the shared-staging cycle.</summary>
internal sealed class PostgresSyncStagingLease : IAsyncDisposable
{
    private const long LockKey = 6003383512567511628;
    private readonly NpgsqlConnection _connection;

    private PostgresSyncStagingLease(NpgsqlConnection connection) => _connection = connection;

    internal static async Task<PostgresSyncStagingLease?> TryAcquireAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var leaseConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        }.ConnectionString;
        var connection = new NpgsqlConnection(leaseConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lockkey);", connection);
            command.Parameters.AddWithValue("lockkey", LockKey);
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
                return new PostgresSyncStagingLease(connection);

            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_connection.State == System.Data.ConnectionState.Open)
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@lockkey);", _connection);
                command.Parameters.AddWithValue("lockkey", LockKey);
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
