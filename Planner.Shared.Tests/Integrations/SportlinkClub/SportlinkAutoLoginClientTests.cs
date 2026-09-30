using System.Net;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

public sealed class SportlinkAutoLoginClientTests
{
    private const string Role = "Wedstrijdzaken";
    private const string ClubMatchBody = "{\"result\":\"ok\"}";

    [Fact]
    public async Task Keep_alive_bootstraps_login_when_no_refresh_token_exists()
    {
        var store = ConfiguredStore();
        var provider = new FakeProvider();
        var http = new RecordingHandler((_, _) => Task.FromResult(Json(ClubMatchBody)));
        var sut = CreateClient(store, provider, http);

        var result = await sut.VerversTokenAsync(Role);

        Assert.Equal(SportlinkClubCallStatus.Ok, result);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("refresh-1", store.RefreshToken);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Invalid_grant_forces_login_when_last_login_is_older_than_fifteen_minutes()
    {
        var store = ConfiguredStore(lastLoginUtc: DateTimeOffset.UtcNow.AddMinutes(-20), refreshToken: "old-refresh");
        var provider = new FakeProvider();
        var tokenCalls = 0;
        var http = new RecordingHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "idm.sportlink.com")
            {
                tokenCalls++;
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("old-refresh", form);
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
                };
            }
            return Json(ClubMatchBody);
        });
        var sut = CreateClient(store, provider, http);

        var result = await sut.VerversTokenAsync(Role);

        Assert.Equal(SportlinkClubCallStatus.Ok, result);
        Assert.Equal(1, tokenCalls);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("refresh-1", store.RefreshToken);
    }

    [Fact]
    public async Task Same_client_does_not_reuse_cached_token_after_credentials_change_or_delete()
    {
        var store = ConfiguredStore();
        var provider = new FakeProvider();
        var authorizationValues = new List<string?>();
        var matchCalls = 0;
        var http = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "club.sportlink.com")
            {
                matchCalls++;
                authorizationValues.Add(request.Headers.Authorization?.Parameter);
            }
            return Task.FromResult(Json(ClubMatchBody));
        });
        var sut = CreateClient(store, provider, http);

        Assert.Equal(SportlinkClubCallStatus.Ok, (await sut.GetMatchRawJsonAsync(Role, "fixture-match")).Status);
        await store.ConfigureAsync(Role, Credentials("replacement-user"), default);
        Assert.Equal(SportlinkClubCallStatus.Ok, (await sut.GetMatchRawJsonAsync(Role, "fixture-match")).Status);
        await store.DeleteCredentialsAsync(Role, default);
        var afterDelete = await sut.GetMatchRawJsonAsync(Role, "fixture-match");

        Assert.Equal(SportlinkClubCallStatus.RolNietGekoppeld, afterDelete.Status);
        Assert.Equal(2, matchCalls);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal("access-1", authorizationValues[0]);
        Assert.Equal("access-2", authorizationValues[1]);
    }

    [Fact]
    public async Task Refresh_token_persistence_failure_stops_before_downstream_api_call()
    {
        var store = ConfiguredStore();
        store.ThrowOnRefreshWrite = true;
        var provider = new FakeProvider();
        var downstreamCalls = 0;
        var http = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "club.sportlink.com") downstreamCalls++;
            return Task.FromResult(Json(ClubMatchBody));
        });
        var sut = CreateClient(store, provider, http);

        var result = await sut.GetMatchRawJsonAsync(Role, "fixture-match");

        Assert.NotEqual(SportlinkClubCallStatus.Ok, result.Status);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, store.RefreshWriteAttempts);
        Assert.Equal(0, downstreamCalls);
    }

    [Fact]
    public async Task Independent_clients_share_lease_and_only_one_bootstraps_login()
    {
        var store = ConfiguredStore();
        var provider = new FakeProvider { Delay = TimeSpan.FromMilliseconds(100) };
        var http = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "idm.sportlink.com")
                return Task.FromResult(Json("{\"access_token\":\"refreshed-access\",\"expires_in\":3600}"));
            return Task.FromResult(Json(ClubMatchBody));
        });
        var firstClient = CreateClient(store, provider, http);
        var secondClient = CreateClient(store, provider, http);

        var results = await Task.WhenAll(
            firstClient.GetMatchRawJsonAsync(Role, "fixture-match"),
            secondClient.GetMatchRawJsonAsync(Role, "fixture-match"));

        Assert.All(results, result => Assert.Equal(SportlinkClubCallStatus.Ok, result.Status));
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, store.SuccessCount);
    }

    private static SportlinkAutoLoginState CredentialsState(DateTimeOffset? lastLoginUtc = null) => new()
    {
        Credentials = Credentials(),
        LastLoginUtc = lastLoginUtc
    };

    private static TestStore ConfiguredStore(DateTimeOffset? lastLoginUtc = null, string? refreshToken = null) => new()
    {
        State = CredentialsState(lastLoginUtc),
        RefreshToken = refreshToken
    };

    private static SportlinkLoginCredentials Credentials(string username = "configured-user") => new()
    {
        Username = username,
        Password = "dummy-password-value",
        TotpSecret = "JBSWY3DPEHPK3PXP"
    };

    private static SportlinkClubClient CreateClient(TestStore store, FakeProvider provider, RecordingHandler handler)
    {
        var coordinator = new SportlinkAutoLoginCoordinator(store, provider);
        return new SportlinkClubClient(new HttpClient(handler), store, NullLogger<SportlinkClubClient>.Instance,
            autoLogin: coordinator);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return respond(request, cancellationToken);
        }
    }

    private sealed class FakeProvider : ISportlinkAutoLoginProvider
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TimeSpan Delay { get; init; }

        public async Task<SportlinkLoginResult> LoginAsync(SportlinkLoginCredentials credentials, CancellationToken cancellationToken = default)
        {
            var number = Interlocked.Increment(ref _callCount);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return new SportlinkLoginResult($"access-{number}", $"refresh-{number}", 3600, 36000);
        }
    }

    private sealed class TestStore : ISportlinkAutoLoginStore
    {
        private readonly SemaphoreSlim _lease = new(1, 1);
        public string ClubCode => "fixture-primary-club";
        public SportlinkAutoLoginState? State { get; set; }
        public string? RefreshToken { get; set; }
        public bool ThrowOnRefreshWrite { get; set; }
        public int RefreshWriteAttempts { get; private set; }
        public int SuccessCount { get; private set; }

        public async Task<IAsyncDisposable> AcquireLeaseAsync(string role, CancellationToken cancellationToken)
        {
            await _lease.WaitAsync(cancellationToken);
            return new Lease(_lease);
        }

        public Task<SportlinkAutoLoginState?> ReadAsync(string role, CancellationToken cancellationToken) => Task.FromResult(State);

        public Task ConfigureAsync(string role, SportlinkLoginCredentials credentials, CancellationToken cancellationToken)
        {
            State = new SportlinkAutoLoginState { Credentials = credentials };
            RefreshToken = null;
            return Task.CompletedTask;
        }

        public Task DeleteCredentialsAsync(string role, CancellationToken cancellationToken)
        {
            State = null;
            RefreshToken = null;
            return Task.CompletedTask;
        }

        public Task MarkAttemptAsync(string role, DateTimeOffset retryAfter, CancellationToken cancellationToken)
        {
            State = new SportlinkAutoLoginState
            {
                Credentials = State!.Credentials,
                LastLoginUtc = State.LastLoginUtc,
                RetryAfterUtc = retryAfter,
                FailureCount = State.FailureCount + 1
            };
            return Task.CompletedTask;
        }

        public Task MarkFailureAsync(string role, string safeCode, bool permanent, CancellationToken cancellationToken)
        {
            State = new SportlinkAutoLoginState
            {
                Credentials = State!.Credentials,
                LastLoginUtc = State.LastLoginUtc,
                RetryAfterUtc = State.RetryAfterUtc,
                FailureCount = State.FailureCount,
                LastError = safeCode
            };
            return Task.CompletedTask;
        }

        public Task MarkSuccessAsync(string role, DateTimeOffset now, CancellationToken cancellationToken)
        {
            SuccessCount++;
            State = new SportlinkAutoLoginState { Credentials = State!.Credentials, LastLoginUtc = now };
            return Task.CompletedTask;
        }

        public string? LeesRefreshToken(string functioneleRol) => RefreshToken;

        public Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default)
        {
            RefreshWriteAttempts++;
            if (ThrowOnRefreshWrite) throw new InvalidOperationException("Fixture persistence failure.");
            RefreshToken = nieuwRefreshToken;
            return Task.CompletedTask;
        }
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
