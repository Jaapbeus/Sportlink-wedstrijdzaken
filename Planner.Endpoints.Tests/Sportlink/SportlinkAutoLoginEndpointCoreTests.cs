using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Endpoints.Tests.Sportlink;

public sealed class SportlinkAutoLoginEndpointCoreTests
{
    private const string Club = "club-primary";
    private const string Username = "test-user-value";
    private const string Password = "test-password-value";
    // Public RFC 6238 Appendix B SHA1 test key, Base32 encoded; synthetic fixture only.
    private const string SyntheticRfc6238Seed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Fact]
    public async Task Missing_store_returns_503_without_exposing_input()
    {
        var context = CreateContext("GET");
        var result = await SportlinkAutoLoginEndpointCore.ExecuteAsync(context.Request, Club, "Wedstrijdzaken", null);

        Assert.Equal(503, Status(result));
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.DoesNotContain(Password, Json(result));
    }

    [Fact]
    public async Task Other_club_is_rejected_before_store_access()
    {
        var store = new FakeStore();
        var context = CreateContext("GET");

        var result = await SportlinkAutoLoginEndpointCore.ExecuteAsync(context.Request, "another-club", "Wedstrijdzaken", store);

        Assert.Equal(403, Status(result));
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, store.LeaseCount);
    }

    [Fact]
    public async Task Role_is_canonicalized_and_status_never_returns_secrets()
    {
        var store = new FakeStore { State = ConfiguredState() };
        var context = CreateContext("GET");

        var result = await SportlinkAutoLoginEndpointCore.ExecuteAsync(context.Request, Club, "wedstrijdzaken", store);
        var body = Json(result);

        Assert.Equal(200, Status(result));
        Assert.Equal("Wedstrijdzaken", store.LastRole);
        Assert.Contains("Configured", body);
        Assert.DoesNotContain(Username, body);
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain(SyntheticRfc6238Seed, body);
    }

    [Fact]
    public async Task Unknown_role_is_rejected()
    {
        var store = new FakeStore();
        var result = await SportlinkAutoLoginEndpointCore.ExecuteAsync(CreateContext("GET").Request, Club, "veldmeester", store);

        Assert.Equal(400, Status(result));
        Assert.Equal(0, store.LeaseCount);
    }

    [Fact]
    public async Task Put_validates_and_stores_credentials_without_echoing_them()
    {
        var store = new FakeStore();
        var context = CreateContext("PUT", CredentialsJson());

        var result = await SportlinkAutoLoginEndpointCore.ExecuteAsync(context.Request, Club, "Wedstrijdzaken", store);
        var body = Json(result);

        Assert.Equal(200, Status(result));
        Assert.Equal(1, store.ConfigureCount);
        Assert.Equal(Username, store.StoredCredentials!.Username);
        Assert.Equal(Password, store.StoredCredentials.Password);
        Assert.Equal(SyntheticRfc6238Seed, store.StoredCredentials.TotpSecret);
        Assert.DoesNotContain(Username, body);
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain(SyntheticRfc6238Seed, body);
    }

    [Fact]
    public async Task Put_rejects_invalid_credentials_without_storing()
    {
        var store = new FakeStore();
        var context = CreateContext("PUT", "{\"username\":\"user\",\"password\":\"pass\",\"totpSecret\":\"invalid!\"}");

        var result = await SportlinkAutoLoginEndpointCore.ExecuteAsync(context.Request, Club, "Wedstrijdzaken", store);

        Assert.Equal(400, Status(result));
        Assert.Equal(0, store.ConfigureCount);
        Assert.DoesNotContain("invalid!", Json(result));
    }

    [Fact]
    public async Task Put_rejects_body_larger_than_8192_bytes()
    {
        var store = new FakeStore();
        var context = CreateContext("PUT", CredentialsJson() + new string(' ', 8193));

        var result = await SportlinkAutoLoginEndpointCore.ExecuteAsync(context.Request, Club, "Wedstrijdzaken", store);

        Assert.Equal(413, Status(result));
        Assert.Equal(0, store.ConfigureCount);
    }

    [Fact]
    public async Task Put_rejects_multibyte_json_that_exceeds_byte_limit()
    {
        var store = new FakeStore();
        var body = CredentialsJson().Replace("}", ",\"ignored\":\"" + new string('é', 5000) + "\"}");
        Assert.True(Encoding.UTF8.GetByteCount(body) > 8192);
        var context = CreateContext("PUT", body);

        var result = await SportlinkAutoLoginEndpointCore.ExecuteAsync(context.Request, Club, "Wedstrijdzaken", store);

        Assert.Equal(413, Status(result));
        Assert.Equal(0, store.ConfigureCount);
    }

    [Fact]
    public async Task Delete_removes_credentials_and_returns_only_status()
    {
        var store = new FakeStore { State = ConfiguredState() };
        var context = CreateContext("DELETE");

        var result = await SportlinkAutoLoginEndpointCore.ExecuteAsync(context.Request, Club, "Wedstrijdzaken", store);
        var body = Json(result);

        Assert.Equal(200, Status(result));
        Assert.Equal(1, store.DeleteCount);
        Assert.DoesNotContain(Username, body);
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain(SyntheticRfc6238Seed, body);
    }

    private static SportlinkAutoLoginState ConfiguredState() => new()
    {
        Credentials = CreateSyntheticCredentials()
    };

    private static SportlinkLoginCredentials CreateSyntheticCredentials() => JsonSerializer.Deserialize<SportlinkLoginCredentials>(
        $"{{\"username\":\"{Username}\",\"password\":\"{Password}\",\"totpSecret\":\"{SyntheticRfc6238Seed}\"}}",
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string CredentialsJson() => JsonSerializer.Serialize(CreateSyntheticCredentials(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static DefaultHttpContext CreateContext(string method, string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
        }
        return context;
    }

    private static int? Status(IActionResult result) => result switch
    {
        ObjectResult obj => obj.StatusCode ?? 200,
        StatusCodeResult status => status.StatusCode,
        _ => null
    };

    private static string Json(IActionResult result) => JsonSerializer.Serialize(((ObjectResult)result).Value);

    private sealed class FakeStore : ISportlinkAutoLoginStore
    {
        public string ClubCode => Club;
        public SportlinkAutoLoginState? State { get; set; }
        public int ReadCount { get; private set; }
        public int LeaseCount { get; private set; }
        public int ConfigureCount { get; private set; }
        public int DeleteCount { get; private set; }
        public string? LastRole { get; private set; }
        public SportlinkLoginCredentials? StoredCredentials { get; private set; }
        public Task<IAsyncDisposable> AcquireLeaseAsync(string role, CancellationToken cancellationToken)
        {
            LeaseCount++;
            LastRole = role;
            return Task.FromResult<IAsyncDisposable>(new NoopLease());
        }
        public Task<SportlinkAutoLoginState?> ReadAsync(string role, CancellationToken cancellationToken)
        {
            ReadCount++;
            LastRole = role;
            return Task.FromResult(State);
        }
        public Task ConfigureAsync(string role, SportlinkLoginCredentials credentials, CancellationToken cancellationToken)
        {
            ConfigureCount++;
            LastRole = role;
            StoredCredentials = credentials;
            State = new SportlinkAutoLoginState { Credentials = credentials };
            return Task.CompletedTask;
        }
        public Task DeleteCredentialsAsync(string role, CancellationToken cancellationToken)
        {
            DeleteCount++;
            LastRole = role;
            State = null;
            return Task.CompletedTask;
        }
        public Task MarkAttemptAsync(string role, DateTimeOffset retryAfter, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkFailureAsync(string role, string safeCode, bool permanent, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkSuccessAsync(string role, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public string? LeesRefreshToken(string functioneleRol) => null;
        public Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
