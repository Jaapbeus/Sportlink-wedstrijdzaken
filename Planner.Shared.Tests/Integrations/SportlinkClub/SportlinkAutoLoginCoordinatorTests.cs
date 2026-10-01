using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

public sealed class SportlinkAutoLoginCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Provider_failure_sets_cooldown_and_prevents_an_immediate_retry()
    {
        var store = ConfiguredStore();
        var provider = new FakeProvider { Exception = new SportlinkLoginException(SportlinkLoginFailure.NetworkFailure), Store = store };
        var clock = new ManualTimeProvider(Now);
        var coordinator = new SportlinkAutoLoginCoordinator(store, provider, clock);

        await Assert.ThrowsAsync<SportlinkLoginException>(() => coordinator.TryLoginAsync("Wedstrijdzaken", force: false, default));
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, store.MarkAttemptCount);
        Assert.Equal(1, store.MarkFailureCount);
        Assert.Equal("Aanmelden tijdelijk mislukt", store.State!.LastError);

        var retry = await coordinator.TryLoginAsync("Wedstrijdzaken", force: false, default);
        Assert.Null(retry);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Automatic_login_runs_at_nine_hours_since_last_success()
    {
        var store = ConfiguredStore();
        store.State = Copy(store.State!, lastLogin: Now.AddHours(-9));
        var provider = new FakeProvider { Store = store };
        var coordinator = new SportlinkAutoLoginCoordinator(store, provider, new ManualTimeProvider(Now));

        var result = await coordinator.TryLoginAsync("Wedstrijdzaken", force: false, default);

        Assert.NotNull(result);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(Now, store.State!.LastLoginUtc);
    }

    [Fact]
    public async Task Forced_login_is_suppressed_until_fifteen_minutes_after_last_success()
    {
        var store = ConfiguredStore();
        store.State = Copy(store.State!, lastLogin: Now.AddMinutes(-14));
        var provider = new FakeProvider { Store = store };
        var clock = new ManualTimeProvider(Now);
        var coordinator = new SportlinkAutoLoginCoordinator(store, provider, clock);

        Assert.Null(await coordinator.TryLoginAsync("Wedstrijdzaken", force: true, default));
        Assert.Equal(0, provider.CallCount);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await coordinator.TryLoginAsync("Wedstrijdzaken", force: true, default));
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task No_configured_credentials_makes_no_provider_request()
    {
        var store = new FakeStore { State = new SportlinkAutoLoginState() };
        var provider = new FakeProvider { Store = store };
        var coordinator = new SportlinkAutoLoginCoordinator(store, provider, new ManualTimeProvider(Now));

        var result = await coordinator.TryLoginAsync("Wedstrijdzaken", force: false, default);

        Assert.Null(result);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, store.MarkAttemptCount);
    }

    [Fact]
    public async Task Refresh_token_is_persisted_before_login_is_marked_successful()
    {
        var store = ConfiguredStore();
        var provider = new FakeProvider { Store = store };
        var coordinator = new SportlinkAutoLoginCoordinator(store, provider, new ManualTimeProvider(Now));

        var result = await coordinator.TryLoginAsync("Wedstrijdzaken", force: false, default);

        Assert.NotNull(result);
        Assert.Equal(new[] { "attempt", "provider", "refresh", "success" }, store.Events);
    }

    [Fact]
    public async Task Three_recorded_failures_suppress_further_login_attempts()
    {
        var store = ConfiguredStore();
        store.State = new SportlinkAutoLoginState
        {
            Credentials = store.State!.Credentials,
            FailureCount = 3
        };
        var provider = new FakeProvider();
        var coordinator = new SportlinkAutoLoginCoordinator(store, provider, new ManualTimeProvider(Now));

        Assert.Null(await coordinator.TryLoginAsync("Wedstrijdzaken", force: true, default));
        Assert.Equal(0, provider.CallCount);
    }

    private static FakeStore ConfiguredStore() => new()
    {
        State = new SportlinkAutoLoginState
        {
            Credentials = new SportlinkLoginCredentials { Username = "test-user", Password = "test-password", TotpSecret = "JBSWY3DPEHPK3PXP" }
        }
    };

    private static SportlinkAutoLoginState Copy(SportlinkAutoLoginState state, DateTimeOffset? lastLogin) => new()
    {
        Credentials = state.Credentials,
        LastLoginUtc = lastLogin,
        RetryAfterUtc = state.RetryAfterUtc,
        FailureCount = state.FailureCount
    };

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan by) => _utcNow += by;
    }

    private sealed class FakeProvider : ISportlinkAutoLoginProvider
    {
        public int CallCount { get; private set; }
        public Exception? Exception { get; init; }
        public FakeStore? Store { get; init; }
        public Task<SportlinkLoginResult> LoginAsync(SportlinkLoginCredentials credentials, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Store?.Events.Add("provider");
            if (Exception is not null) throw Exception;
            return Task.FromResult(new SportlinkLoginResult("access-test", "refresh-test", 3600, 36000));
        }
    }

    private sealed class FakeStore : ISportlinkAutoLoginStore
    {
        public string ClubCode => "club-primary";
        public SportlinkAutoLoginState? State { get; set; }
        public List<string> Events { get; } = [];
        public int MarkAttemptCount { get; private set; }
        public int MarkFailureCount { get; private set; }
        public Task<IAsyncDisposable> AcquireLeaseAsync(string role, CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(new Lease());
        public Task<SportlinkAutoLoginState?> ReadAsync(string role, CancellationToken cancellationToken) => Task.FromResult(State);
        public Task ConfigureAsync(string role, SportlinkLoginCredentials credentials, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteCredentialsAsync(string role, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkAttemptAsync(string role, DateTimeOffset retryAfter, CancellationToken cancellationToken)
        {
            MarkAttemptCount++;
            Events.Add("attempt");
            State = new SportlinkAutoLoginState { Credentials = State!.Credentials, LastLoginUtc = State.LastLoginUtc, RetryAfterUtc = retryAfter, FailureCount = State.FailureCount };
            return Task.CompletedTask;
        }
        public Task MarkFailureAsync(string role, string safeCode, bool permanent, CancellationToken cancellationToken)
        {
            MarkFailureCount++;
            State = new SportlinkAutoLoginState
            {
                Credentials = State!.Credentials, LastLoginUtc = State.LastLoginUtc,
                RetryAfterUtc = permanent || MarkFailureCount >= 3 ? DateTimeOffset.MaxValue : State.RetryAfterUtc,
                FailureCount = State.FailureCount + 1, LastError = safeCode
            };
            return Task.CompletedTask;
        }
        public Task MarkSuccessAsync(string role, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Events.Add("success");
            State = new SportlinkAutoLoginState { Credentials = State!.Credentials, LastLoginUtc = now, FailureCount = 0 };
            return Task.CompletedTask;
        }
        public string? LeesRefreshToken(string functioneleRol) => null;
        public Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
        {
            Events.Add("refresh");
            return Task.CompletedTask;
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
