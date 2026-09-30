using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Database.Postgres.Tests;
using FunctionApp.Postgres.Sportlink;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace FunctionApp.Postgres.Tests;

/// <summary>PostgreSQL-integratietests voor versleutelde auto-login-opslag en transactionele leases.</summary>
public sealed class PostgresSportlinkAutoLoginStoreIntegrationTests
{
    // Public RFC 6238 Appendix B SHA1 test key, Base32 encoded; synthetic fixture only.
    private const string SyntheticRfc6238Seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private static string ConnectionString => PostgresTestEnvironment.ConnectionStringOrNull
        ?? throw new InvalidOperationException(
            $"{PostgresTestEnvironment.ConnectionStringEnvVar} niet gezet — zie klasse-doc-comment.");

    [PostgresFact]
    public async Task Configure_RoundtripSlaatAlleenCiphertextOp_EnResetFailureState()
    {
        await using var scope = await TestScope.CreateAsync();
        var credentials = NieuweCredentials();

        await scope.Store.ConfigureAsync(scope.Role, credentials, CancellationToken.None);
        await scope.Store.SchrijfRefreshTokenAsync(scope.Role, "test-refresh-secret");
        var now = DateTimeOffset.UtcNow;
        await scope.Store.MarkSuccessAsync(scope.Role, now, CancellationToken.None);

        var raw = await LeesGeheimenAsync(scope.Club, scope.Role);
        raw.Credentials.Should().NotBeNull();
        raw.Refresh.Should().NotBeNull();
        raw.Credentials.Should().NotContain(credentials.Username);
        raw.Credentials.Should().NotContain(credentials.Password);
        raw.Credentials.Should().NotContain(credentials.TotpSecret);
        raw.Refresh.Should().NotContain("test-refresh-secret");

        var read = await scope.Store.ReadAsync(scope.Role, CancellationToken.None);
        read.Should().NotBeNull();
        read!.Credentials!.Username.Should().Be(credentials.Username);
        read.Credentials.Password.Should().Be(credentials.Password);
        read.Credentials.TotpSecret.Should().Be(credentials.TotpSecret);
        read.LastLoginUtc.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(10));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await scope.Store.MarkAttemptAsync(scope.Role, now.AddMinutes(15), CancellationToken.None);
            await scope.Store.MarkFailureAsync(scope.Role, "InvalidCredentials", permanent: false, CancellationToken.None);
        }

        read = await scope.Store.ReadAsync(scope.Role, CancellationToken.None);
        read!.FailureCount.Should().Be(3);
        read.LastError.Should().Be("InvalidCredentials");
        read.RetryAfterUtc!.Value.Year.Should().Be(9999);

        await scope.Store.ConfigureAsync(scope.Role, NieuweCredentials(), CancellationToken.None);
        read = await scope.Store.ReadAsync(scope.Role, CancellationToken.None);
        read!.FailureCount.Should().Be(0);
        read.LastError.Should().BeNull();
        read.RetryAfterUtc.Should().BeNull();
        read.LastLoginUtc.Should().BeNull();
    }

    [PostgresFact]
    public async Task PermanenteFout_StoptVerdereAanmeldpogingenTotConfiguratieWordtVervangen()
    {
        await using var scope = await TestScope.CreateAsync();
        await scope.Store.ConfigureAsync(scope.Role, NieuweCredentials(), CancellationToken.None);

        await scope.Store.MarkAttemptAsync(scope.Role, DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None);
        await scope.Store.MarkFailureAsync(scope.Role, "TotpRejected", permanent: true, CancellationToken.None);

        var state = await scope.Store.ReadAsync(scope.Role, CancellationToken.None);
        state!.RetryAfterUtc!.Value.Year.Should().Be(9999);
        state.FailureCount.Should().Be(1);
        state.LastError.Should().Be("TotpRejected");
    }

    [PostgresFact]
    public async Task DeleteCredentials_WistBeideCiphertexts_EnRoeptLegacyFallbackNietAan()
    {
        var legacy = new FakeLegacyTokenStore("legacy-refresh-secret");
        await using var scope = await TestScope.CreateAsync(legacy: legacy);
        await scope.Store.ConfigureAsync(scope.Role, NieuweCredentials(), CancellationToken.None);
        await scope.Store.SchrijfRefreshTokenAsync(scope.Role, "stored-refresh-secret");

        await scope.Store.ConfigureAsync(scope.Role, NieuweCredentials(), CancellationToken.None);
        scope.Store.LeesRefreshToken(scope.Role).Should().BeNull(
            "nieuwe logincredentials mogen geen refresh-token van het vorige account blijven gebruiken");
        legacy.ReadCount.Should().Be(0);
        await scope.Store.SchrijfRefreshTokenAsync(scope.Role, "stored-refresh-secret");

        await scope.Store.DeleteCredentialsAsync(scope.Role, CancellationToken.None);

        var raw = await LeesGeheimenAsync(scope.Club, scope.Role);
        raw.Credentials.Should().BeNull();
        raw.Refresh.Should().BeNull();
        (await scope.Store.ReadAsync(scope.Role, CancellationToken.None))!.Credentials.Should().BeNull();
        scope.Store.LeesRefreshToken(scope.Role).Should().BeNull();
        legacy.ReadCount.Should().Be(0, "een tombstone-rij voorkomt dat een oud token na verwijderen terugkomt");
    }

    [PostgresFact]
    public async Task OntbrekendeRijGebruiktLegacyToken_EnClubcontextIsolereertCiphertext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var first = await TestScope.CreateAsync(key: key);
        var legacy = new FakeLegacyTokenStore("legacy-refresh-secret");
        await using var second = await TestScope.CreateAsync(role: first.Role, key: key, legacy: legacy);

        (await second.Store.ReadAsync(second.Role, CancellationToken.None)).Should().BeNull();
        second.Store.LeesRefreshToken(second.Role).Should().Be("legacy-refresh-secret");
        legacy.ReadCount.Should().Be(1);

        await first.Store.ConfigureAsync(first.Role, NieuweCredentials(), CancellationToken.None);
        var encrypted = (await LeesGeheimenAsync(first.Club, first.Role)).Credentials!;
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                INSERT INTO public.sportlinkautologin (clubcode, rolnaam, credentialsencrypted)
                VALUES (@club, @role, @ciphertext)", connection);
            command.Parameters.AddWithValue("club", second.Club);
            command.Parameters.AddWithValue("role", first.Role);
            command.Parameters.AddWithValue("ciphertext", encrypted);
            await command.ExecuteNonQueryAsync();
        }

        var act = async () => await second.Store.ReadAsync(first.Role, CancellationToken.None);
        await Assert.ThrowsAsync<CryptographicException>(act);
    }

    [PostgresFact]
    public async Task Lease_SerialiseertAparteStoreInstanties_EnVrijgaveRolledBackTransactie()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var first = await TestScope.CreateAsync(key: key);
        await using var second = await TestScope.CreateAsync(club: first.Club, role: first.Role, key: key);

        await using (var warmConnection = await second.Store.AcquireLeaseAsync("warmup", CancellationToken.None)) { }
        var lease = await first.Store.AcquireLeaseAsync(first.Role, CancellationToken.None);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            var blocked = second.Store.AcquireLeaseAsync(second.Role, cancellation.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blocked);
        }

        await lease.DisposeAsync(); // transaction rollback releases pg_advisory_xact_lock
        using var retryCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var acquiredAfterRollback = await second.Store.AcquireLeaseAsync(second.Role, retryCancellation.Token);
    }

    private static SportlinkLoginCredentials NieuweCredentials()
    {
        var fixtureJson = $$"""{"username":"test-user-{{Guid.NewGuid():N}}","password":"test-password-{{Guid.NewGuid():N}}","totpSecret":"{{SyntheticRfc6238Seed}}","totpAlgorithm":"SHA1","totpDigits":6,"totpPeriodSeconds":30}""";
        return JsonSerializer.Deserialize<SportlinkLoginCredentials>(fixtureJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<(string? Credentials, string? Refresh)> LeesGeheimenAsync(string club, string role)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
            SELECT credentialsencrypted, refreshencrypted
            FROM public.sportlinkautologin WHERE clubcode = @club AND rolnaam = @role", connection);
        command.Parameters.AddWithValue("club", club);
        command.Parameters.AddWithValue("role", role);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly byte[] _key;
        private readonly FakeLegacyTokenStore _legacy;
        public string Club { get; }
        public string Role { get; }
        public PostgresSportlinkAutoLoginStore Store { get; }

        private TestScope(string club, string role, byte[] key, FakeLegacyTokenStore legacy)
        {
            Club = club;
            Role = role;
            _key = key;
            _legacy = legacy;
            Store = new PostgresSportlinkAutoLoginStore(
                ConnectionString, () => Club, new SportlinkCredentialProtector(_key), _legacy);
        }

        public static async Task<TestScope> CreateAsync(
            string? club = null, string? role = null, byte[]? key = null, FakeLegacyTokenStore? legacy = null)
        {
            var scope = new TestScope(
                club ?? "al" + Guid.NewGuid().ToString("N")[..16],
                role ?? "r" + Guid.NewGuid().ToString("N")[..20],
                key ?? RandomNumberGenerator.GetBytes(32),
                legacy ?? new FakeLegacyTokenStore(null));
            await scope.DeleteRowsAsync();
            return scope;
        }

        private async Task DeleteRowsAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM public.sportlinkautologin WHERE clubcode = @club", connection);
            command.Parameters.AddWithValue("club", Club);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try { await DeleteRowsAsync(); }
            finally { CryptographicOperations.ZeroMemory(_key); }
        }
    }

    private sealed class FakeLegacyTokenStore(string? token) : ISportlinkClubTokenStore
    {
        public int ReadCount { get; private set; }
        public string? LeesRefreshToken(string functioneleRol)
        {
            ReadCount++;
            return token;
        }
        public Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
