using Microsoft.Data.SqlClient;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction;

/// <summary>Holds one database-wide SQL Server session lock for the full shared-staging cycle.</summary>
internal sealed class SqlServerSyncStagingLease : IAsyncDisposable
{
    private const string Resource = "sportlink-sync-staging-v1";
    private readonly SqlConnection _connection;

    private SqlServerSyncStagingLease(SqlConnection connection) => _connection = connection;

    internal static async Task<SqlServerSyncStagingLease?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(DatabaseConfig.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                "DECLARE @result int; EXEC @result = sys.sp_getapplock " +
                "@Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; " +
                "SELECT @result;", connection);
            command.Parameters.AddWithValue("@resource", Resource);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (result >= 0)
                return new SqlServerSyncStagingLease(connection);

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
                await using var command = new SqlCommand(
                    "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';", _connection);
                command.Parameters.AddWithValue("@resource", Resource);
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
