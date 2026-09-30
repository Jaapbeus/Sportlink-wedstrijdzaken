using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>PostgreSQL-opslag voor versleutelde automatische Sportlink-login per club en rol.</summary>
public sealed class PostgresSportlinkAutoLoginStore : ISportlinkAutoLoginStore
{
    private readonly string _connectionString;
    private readonly Func<string> _clubCode;
    private readonly SportlinkCredentialProtector _protector;
    private readonly ISportlinkClubTokenStore _legacy;

    public PostgresSportlinkAutoLoginStore(
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
        var connection = new NpgsqlConnection(_connectionString);
        NpgsqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken);
            transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction))
                await timeout.ExecuteNonQueryAsync(cancellationToken);
            await using (var command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction))
            {
                command.Parameters.AddWithValue("key", $"sportlink-auto-login:{ClubCode}:{role}");
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
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
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(@"
            SELECT credentialsencrypted, lastloginutc, retryafterutc, lasterror, failurecount
            FROM public.sportlinkautologin
            WHERE clubcode = @clubcode AND rolnaam = @rolnaam", connection);
        command.Parameters.AddWithValue("clubcode", club);
        command.Parameters.AddWithValue("rolnaam", role);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var encryptedCredentials = reader.IsDBNull(0) ? null : reader.GetString(0);
        return new SportlinkAutoLoginState
        {
            Credentials = encryptedCredentials == null ? null : _protector.Unprotect(encryptedCredentials, club, role),
            LastLoginUtc = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime(),
            RetryAfterUtc = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime(),
            LastError = reader.IsDBNull(3) ? null : reader.GetString(3),
            FailureCount = reader.GetInt32(4)
        };
    }

    public async Task ConfigureAsync(string role, SportlinkLoginCredentials credentials, CancellationToken cancellationToken)
    {
        var club = ClubCode;
        var encrypted = _protector.Protect(credentials, club, role);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(@"
            INSERT INTO public.sportlinkautologin
                (clubcode, rolnaam, credentialsencrypted, lastloginutc, retryafterutc, lasterror)
            VALUES (@clubcode, @rolnaam, @credentials, NULL, NULL, NULL)
            ON CONFLICT (clubcode, rolnaam) DO UPDATE SET
                credentialsencrypted = EXCLUDED.credentialsencrypted,
                refreshencrypted = NULL, lastloginutc = NULL, retryafterutc = NULL,
                lasterror = NULL, failurecount = 0", connection);
        command.Parameters.AddWithValue("clubcode", club);
        command.Parameters.AddWithValue("rolnaam", role);
        command.Parameters.AddWithValue("credentials", encrypted);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteCredentialsAsync(string role, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(@"
            UPDATE public.sportlinkautologin
            SET credentialsencrypted = NULL, refreshencrypted = NULL, lastloginutc = NULL,
                retryafterutc = NULL, lasterror = NULL, failurecount = 0
            WHERE clubcode = @clubcode AND rolnaam = @rolnaam", connection);
        command.Parameters.AddWithValue("clubcode", ClubCode);
        command.Parameters.AddWithValue("rolnaam", role);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAttemptAsync(string role, DateTimeOffset retryAfter, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(@"
            INSERT INTO public.sportlinkautologin AS stored
                (clubcode, rolnaam, retryafterutc, lasterror, failurecount)
            VALUES (@clubcode, @rolnaam, @retryafterutc, 'Aanmelden vereist', 1)
            ON CONFLICT (clubcode, rolnaam) DO UPDATE SET
                retryafterutc = EXCLUDED.retryafterutc, lasterror = 'Aanmelden vereist',
                failurecount = stored.failurecount + 1", connection);
        command.Parameters.AddWithValue("clubcode", ClubCode);
        command.Parameters.AddWithValue("rolnaam", role);
        command.Parameters.AddWithValue("retryafterutc", retryAfter.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkSuccessAsync(string role, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(@"
            INSERT INTO public.sportlinkautologin (clubcode, rolnaam, lastloginutc, failurecount)
            VALUES (@clubcode, @rolnaam, @lastloginutc, 0)
            ON CONFLICT (clubcode, rolnaam) DO UPDATE SET
                lastloginutc = EXCLUDED.lastloginutc, retryafterutc = NULL, lasterror = NULL, failurecount = 0", connection);
        command.Parameters.AddWithValue("clubcode", ClubCode);
        command.Parameters.AddWithValue("rolnaam", role);
        command.Parameters.AddWithValue("lastloginutc", now.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailureAsync(string role, string safeCode, bool permanent, CancellationToken cancellationToken)
    {
        var code = string.IsNullOrWhiteSpace(safeCode) ? "LoginFailed" : safeCode[..Math.Min(safeCode.Length, 64)];
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(@"
            UPDATE public.sportlinkautologin
            SET lasterror = @code,
                retryafterutc = CASE
                    WHEN @permanent OR failurecount >= 3 THEN TIMESTAMPTZ '9999-12-31 00:00:00+00'
                    ELSE retryafterutc
                END
            WHERE clubcode = @clubcode AND rolnaam = @rolnaam", connection);
        command.Parameters.AddWithValue("clubcode", ClubCode);
        command.Parameters.AddWithValue("rolnaam", role);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("permanent", permanent);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public string? LeesRefreshToken(string functioneleRol)
        => LeesRefreshTokenAsync(functioneleRol).GetAwaiter().GetResult();

    private async Task<string?> LeesRefreshTokenAsync(string role)
    {
        var club = ClubCode;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
            SELECT refreshencrypted FROM public.sportlinkautologin
            WHERE clubcode = @clubcode AND rolnaam = @rolnaam", connection);
        command.Parameters.AddWithValue("clubcode", club);
        command.Parameters.AddWithValue("rolnaam", role);
        var encrypted = await command.ExecuteScalarAsync();
        if (encrypted is null) return _legacy.LeesRefreshToken(role);
        return encrypted is string ciphertext
            ? _protector.UnprotectSecret(ciphertext, club, role, "refresh-token")
            : null;
    }

    public async Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
    {
        var club = ClubCode;
        var encrypted = _protector.ProtectSecret(nieuwRefreshToken, club, functioneleRol, "refresh-token");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(@"
            INSERT INTO public.sportlinkautologin (clubcode, rolnaam, refreshencrypted)
            VALUES (@clubcode, @rolnaam, @refreshencrypted)
            ON CONFLICT (clubcode, rolnaam) DO UPDATE SET refreshencrypted = EXCLUDED.refreshencrypted", connection);
        command.Parameters.AddWithValue("clubcode", club);
        command.Parameters.AddWithValue("rolnaam", functioneleRol);
        command.Parameters.AddWithValue("refreshencrypted", encrypted);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class TransactionLease(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
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
