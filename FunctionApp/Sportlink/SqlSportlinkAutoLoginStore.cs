using Microsoft.Data.SqlClient;
using Planner.Shared.Integrations.SportlinkClub;

namespace SportlinkFunction.Sportlink;

/// <summary>SQL Server-opslag voor versleutelde automatische Sportlink-login per club en rol.</summary>
public sealed class SqlSportlinkAutoLoginStore : ISportlinkAutoLoginStore
{
    private readonly string _connectionString;
    private readonly Func<string> _clubCode;
    private readonly SportlinkCredentialProtector _protector;
    private readonly ISportlinkClubTokenStore _legacy;

    public SqlSportlinkAutoLoginStore(
        string connectionString,
        Func<string> clubCode,
        SportlinkCredentialProtector protector,
        ISportlinkClubTokenStore legacy)
    {
        _connectionString = connectionString;
        _clubCode = clubCode;
        _protector = protector;
        _legacy = legacy;
    }

    public string ClubCode => _clubCode();

    public async Task<IAsyncDisposable> AcquireLeaseAsync(string role, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        SqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken);
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new SqlCommand(@"
                DECLARE @result INT;
                EXEC @result = sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 5000;
                SELECT @result;", connection, transaction);
            command.Parameters.AddWithValue("@resource", $"sportlink-auto-login:{ClubCode}:{role}");
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (result < 0)
                throw new TimeoutException("De automatische Sportlink-loginlock kon niet binnen vijf seconden worden verkregen.");
            return new TransactionLease(connection, transaction);
        }
        catch
        {
            if (transaction != null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<SportlinkAutoLoginState?> ReadAsync(string role, CancellationToken cancellationToken)
    {
        var club = ClubCode;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
            SELECT [CredentialsEncrypted], [LastLoginUtc], [RetryAfterUtc], [LastError], [FailureCount]
            FROM [dbo].[SportlinkAutoLogin]
            WHERE [ClubCode] = @clubcode AND [RolNaam] = @rolnaam", connection);
        AddScope(command, club, role);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var ciphertext = reader.IsDBNull(0) ? null : reader.GetString(0);
        return new SportlinkAutoLoginState
        {
            Credentials = ciphertext == null ? null : _protector.Unprotect(ciphertext, club, role),
            LastLoginUtc = reader.IsDBNull(1) ? null : AsUtc(reader.GetDateTime(1)),
            RetryAfterUtc = reader.IsDBNull(2) ? null : AsUtc(reader.GetDateTime(2)),
            LastError = reader.IsDBNull(3) ? null : reader.GetString(3),
            FailureCount = reader.GetInt32(4)
        };
    }

    public async Task ConfigureAsync(string role, SportlinkLoginCredentials credentials, CancellationToken cancellationToken)
    {
        var club = ClubCode;
        var encrypted = _protector.Protect(credentials, club, role);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
            UPDATE [dbo].[SportlinkAutoLogin]
            SET [CredentialsEncrypted] = @credentials, [RefreshEncrypted] = NULL, [LastLoginUtc] = NULL,
                [RetryAfterUtc] = NULL, [LastError] = NULL, [FailureCount] = 0
            WHERE [ClubCode] = @clubcode AND [RolNaam] = @rolnaam;
            IF @@ROWCOUNT = 0
                INSERT INTO [dbo].[SportlinkAutoLogin]
                    ([ClubCode], [RolNaam], [CredentialsEncrypted], [FailureCount])
                VALUES (@clubcode, @rolnaam, @credentials, 0);", connection);
        AddScope(command, club, role);
        command.Parameters.AddWithValue("@credentials", encrypted);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteCredentialsAsync(string role, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
            UPDATE [dbo].[SportlinkAutoLogin]
            SET [CredentialsEncrypted] = NULL, [LastLoginUtc] = NULL,
                [RefreshEncrypted] = NULL, [RetryAfterUtc] = NULL, [LastError] = NULL, [FailureCount] = 0
            WHERE [ClubCode] = @clubcode AND [RolNaam] = @rolnaam", connection);
        AddScope(command, ClubCode, role);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAttemptAsync(string role, DateTimeOffset retryAfter, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
            UPDATE [dbo].[SportlinkAutoLogin]
            SET [RetryAfterUtc] = @retryafterutc, [LastError] = N'Aanmelden vereist',
                [FailureCount] = [FailureCount] + 1
            WHERE [ClubCode] = @clubcode AND [RolNaam] = @rolnaam;
            IF @@ROWCOUNT = 0
                INSERT INTO [dbo].[SportlinkAutoLogin]
                    ([ClubCode], [RolNaam], [RetryAfterUtc], [LastError], [FailureCount])
                VALUES (@clubcode, @rolnaam, @retryafterutc, N'Aanmelden vereist', 1);", connection);
        AddScope(command, ClubCode, role);
        command.Parameters.AddWithValue("@retryafterutc", retryAfter.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkSuccessAsync(string role, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
            UPDATE [dbo].[SportlinkAutoLogin]
            SET [LastLoginUtc] = @lastloginutc, [RetryAfterUtc] = NULL, [LastError] = NULL, [FailureCount] = 0
            WHERE [ClubCode] = @clubcode AND [RolNaam] = @rolnaam;
            IF @@ROWCOUNT = 0
                INSERT INTO [dbo].[SportlinkAutoLogin] ([ClubCode], [RolNaam], [LastLoginUtc], [FailureCount])
                VALUES (@clubcode, @rolnaam, @lastloginutc, 0);", connection);
        AddScope(command, ClubCode, role);
        command.Parameters.AddWithValue("@lastloginutc", now.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailureAsync(string role, string safeCode, bool permanent, CancellationToken cancellationToken)
    {
        var code = string.IsNullOrWhiteSpace(safeCode) ? "LoginFailed" : safeCode[..Math.Min(safeCode.Length, 64)];
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
            UPDATE [dbo].[SportlinkAutoLogin]
            SET [LastError] = @code,
                [RetryAfterUtc] = CASE
                    WHEN @permanent = 1 OR [FailureCount] >= 3 THEN CONVERT(DATETIME2(7), '9999-12-31T00:00:00')
                    ELSE [RetryAfterUtc]
                END
            WHERE [ClubCode] = @clubcode AND [RolNaam] = @rolnaam", connection);
        AddScope(command, ClubCode, role);
        command.Parameters.AddWithValue("@code", code);
        command.Parameters.AddWithValue("@permanent", permanent);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public string? LeesRefreshToken(string functioneleRol)
        => LeesRefreshTokenAsync(functioneleRol).GetAwaiter().GetResult();

    private async Task<string?> LeesRefreshTokenAsync(string role)
    {
        var club = ClubCode;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(@"
            SELECT [RefreshEncrypted] FROM [dbo].[SportlinkAutoLogin]
            WHERE [ClubCode] = @clubcode AND [RolNaam] = @rolnaam", connection);
        AddScope(command, club, role);
        var value = await command.ExecuteScalarAsync();
        if (value is null) return _legacy.LeesRefreshToken(role);
        return value is string ciphertext
            ? _protector.UnprotectSecret(ciphertext, club, role, "refresh-token")
            : null;
    }

    public async Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
    {
        var club = ClubCode;
        var encrypted = _protector.ProtectSecret(nieuwRefreshToken, club, functioneleRol, "refresh-token");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
            UPDATE [dbo].[SportlinkAutoLogin]
            SET [RefreshEncrypted] = @refreshencrypted
            WHERE [ClubCode] = @clubcode AND [RolNaam] = @rolnaam;
            IF @@ROWCOUNT = 0
                INSERT INTO [dbo].[SportlinkAutoLogin] ([ClubCode], [RolNaam], [RefreshEncrypted])
                VALUES (@clubcode, @rolnaam, @refreshencrypted);", connection);
        AddScope(command, club, functioneleRol);
        command.Parameters.AddWithValue("@refreshencrypted", encrypted);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddScope(SqlCommand command, string club, string role)
    {
        command.Parameters.AddWithValue("@clubcode", club);
        command.Parameters.AddWithValue("@rolnaam", role);
    }

    private static DateTimeOffset AsUtc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class TransactionLease(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await transaction.RollbackAsync(); }
            catch (InvalidOperationException) { }
            finally
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}
