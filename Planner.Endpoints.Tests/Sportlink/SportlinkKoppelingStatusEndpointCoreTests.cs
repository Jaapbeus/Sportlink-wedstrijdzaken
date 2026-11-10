using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Endpoints.Tests.Sportlink;

/// <summary>#1773: koppelingsstatus-endpoint achter de Wedstrijdzaken-poort. Fixtures zijn fictief.</summary>
public sealed class SportlinkKoppelingStatusEndpointCoreTests
{
    private const string Club = "club-primary";
    private static readonly DateTimeOffset Nu = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    // Alleen de aanwezigheid van credentials telt; de waarden worden door het endpoint nooit gelezen of teruggegeven.
    private static readonly SportlinkLoginCredentials Credentials = new()
    {
        Username = "service-account-fixture", Password = "fixture-wachtwoord", TotpSecret = "fixture-sleutel",
    };

    [Fact]
    public async Task Zonder_store_is_niet_gekoppeld_en_no_store()
    {
        var context = new DefaultHttpContext();

        var result = await SportlinkKoppelingStatusEndpointCore.ExecuteAsync(context.Request, Club, null, new VasteTijd(Nu));

        Assert.Equal(200, Status(result));
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        var json = Json(result);
        Assert.Contains("\"Niveau\":\"Verbroken\"", json);
        Assert.Contains("\"IsVerbroken\":true", json);
    }

    [Fact]
    public async Task Andere_club_leest_de_opslag_niet_en_is_niet_gekoppeld()
    {
        var store = new FakeStore { State = new SportlinkAutoLoginState { Credentials = Credentials } };
        var context = new DefaultHttpContext();

        var result = await SportlinkKoppelingStatusEndpointCore.ExecuteAsync(context.Request, "andere-club", store, new VasteTijd(Nu));

        Assert.Equal(0, store.ReadCount);
        Assert.Contains("\"Niveau\":\"Verbroken\"", Json(result));
    }

    [Fact]
    public async Task Geblokkeerde_autologin_is_verbroken_zonder_secrets_in_de_respons()
    {
        var store = new FakeStore
        {
            State = new SportlinkAutoLoginState
            {
                Credentials = Credentials, FailureCount = 3, LastError = "Controleer logininstellingen",
                RetryAfterUtc = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero), LastLoginUtc = Nu.AddDays(-2),
            },
            RefreshToken = "refresh-token-fixture-value",
        };
        var context = new DefaultHttpContext();

        var result = await SportlinkKoppelingStatusEndpointCore.ExecuteAsync(context.Request, Club, store, new VasteTijd(Nu));

        var json = Json(result);
        Assert.Contains("\"Niveau\":\"Verbroken\"", json);
        Assert.Contains("geblokkeerd", json);
        Assert.DoesNotContain(Credentials.Username, json);
        Assert.DoesNotContain(Credentials.Password, json);
        Assert.DoesNotContain(Credentials.TotpSecret, json);
        Assert.DoesNotContain(store.RefreshToken, json);
        Assert.DoesNotContain("LastError", json);
    }

    [Fact]
    public async Task Gezonde_koppeling_is_ok_zonder_melding()
    {
        var store = new FakeStore
        {
            State = new SportlinkAutoLoginState { Credentials = Credentials, LastLoginUtc = Nu.AddHours(-1) },
            RefreshToken = "refresh-token-fixture-value",
        };
        var context = new DefaultHttpContext();

        var result = await SportlinkKoppelingStatusEndpointCore.ExecuteAsync(context.Request, Club, store, new VasteTijd(Nu));

        var json = Json(result);
        Assert.Contains("\"Niveau\":\"Ok\"", json);
        Assert.Contains("\"Melding\":null", json);
    }

    [Fact]
    public async Task Opslagfout_geeft_503_zonder_details()
    {
        var store = new FakeStore { Gooit = true };
        var context = new DefaultHttpContext();

        var result = await SportlinkKoppelingStatusEndpointCore.ExecuteAsync(context.Request, Club, store, new VasteTijd(Nu));

        Assert.Equal(503, Status(result));
        Assert.DoesNotContain("geheim-detail", Json(result));
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
        public string? RefreshToken { get; set; }
        public bool Gooit { get; set; }
        public int ReadCount { get; private set; }
        public Task<IAsyncDisposable> AcquireLeaseAsync(string role, CancellationToken cancellationToken)
            => Task.FromResult<IAsyncDisposable>(new NoopLease());
        public Task<SportlinkAutoLoginState?> ReadAsync(string role, CancellationToken cancellationToken)
        {
            ReadCount++;
            if (Gooit) throw new InvalidOperationException("geheim-detail");
            return Task.FromResult(State);
        }
        public Task ConfigureAsync(string role, SportlinkLoginCredentials credentials, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteCredentialsAsync(string role, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkAttemptAsync(string role, DateTimeOffset retryAfter, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkFailureAsync(string role, string safeCode, bool permanent, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkSuccessAsync(string role, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public string? LeesRefreshToken(string functioneleRol) => RefreshToken;
        public Task SchrijfRefreshTokenAsync(string functioneleRol, string nieuwRefreshToken, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class VasteTijd(DateTimeOffset nu) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nu;
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
