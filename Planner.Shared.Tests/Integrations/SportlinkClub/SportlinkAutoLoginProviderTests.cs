using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Planner.Shared.Integrations.SportlinkClub;
using Xunit;

namespace Planner.Shared.Tests.Integrations.SportlinkClub;

public sealed class SportlinkAutoLoginProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoginAsync_UsernamePasswordAndOtp_ExchangesValidatedCodeWithPkce()
    {
        var requests = new List<(string Method, Uri Uri, string? Body)>();
        var client = MakeProvider(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((request.Method.Method, request.RequestUri!, body));
            if (request.Method == HttpMethod.Get)
                return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate?session_code=fake'><input type='hidden' name='session_code' value='fake'><input type='hidden' name='credentialId' value=''><input name='username'><input type='password' name='password'><input type='checkbox' name='rememberMe'><input name='login' type='submit' value='Sign in'><button type='button' aria-controls='password'>Show password</button></form>");
            if (request.RequestUri!.AbsolutePath.EndsWith("/authenticate", StringComparison.Ordinal))
            {
                var authorize = requests[0].Uri;
                var state = HttpUtility.ParseQueryString(authorize.Query)["state"]!;
                return Redirect($"https://club.sportlink.com/dashboard?code=fake-code&state={Uri.EscapeDataString(state)}&iss=https%3A%2F%2Fidm.sportlink.com%2Frealms%2Fsportlink");
            }
            return Json("""{"access_token":"access-test","refresh_token":"refresh-test","expires_in":300,"refresh_expires_in":36000}""");
        });

        var result = await client.LoginAsync(Credentials());

        Assert.Equal("access-test", result.AccessToken);
        Assert.Equal("refresh-test", result.RefreshToken);
        Assert.Equal(300, result.ExpiresInSeconds);
        Assert.Equal(36000, result.RefreshExpiresInSeconds);
        Assert.Equal(3, requests.Count);
        var authQuery = HttpUtility.ParseQueryString(requests[0].Uri.Query);
        Assert.Equal("S256", authQuery["code_challenge_method"]);
        Assert.Contains("username=test-user", requests[1].Body);
        Assert.Contains(Uri.EscapeDataString("pass" + "word") + "=test-password", requests[1].Body);
        Assert.Contains("login=Sign+in", requests[1].Body);
        Assert.DoesNotContain("rememberMe", requests[1].Body);
        var tokenBody = HttpUtility.ParseQueryString(requests[2].Body!);
        Assert.Equal("fake-code", tokenBody["code"]);
        var verifier = tokenBody["code_verifier"]!;
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(challenge, authQuery["code_challenge"]);
        Assert.Equal("https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token", requests[2].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task LoginAsync_OtpForm_SubmitsOneTotpThenExchangesToken()
    {
        var postCount = 0;
        Uri? initialUri = null;
        string? otpBody = null;
        var provider = MakeProvider(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                initialUri = request.RequestUri;
                return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate'><input name='username'><input type='password' name='password'><input name='login' type='submit' value='Sign in'></form>");
            }
            postCount++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/authenticate", StringComparison.Ordinal) && postCount == 1)
                return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate'><input type='hidden' name='execution' value='x'><input name='otp' autocomplete='one-time-code'><input name='login' type='submit' value='Sign in'></form>");
            if (postCount == 2)
            {
                otpBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var state = HttpUtility.ParseQueryString(initialUri!.Query)["state"]!;
                return Redirect($"https://club.sportlink.com/dashboard?code=otp-code&state={Uri.EscapeDataString(state)}");
            }
            return Json("""{"access_token":"a","refresh_token":"r","expires_in":30}""");
        });

        var result = await provider.LoginAsync(Credentials());
        Assert.Equal("a", result.AccessToken);
        Assert.Equal(SportlinkTotp.Generate("JBSWY3DPEHPK3PXP", Now), HttpUtility.ParseQueryString(otpBody!)["otp"]);
        Assert.Equal(3, postCount);
    }

    [Fact]
    public async Task LoginAsync_ExternalFormAction_FailsBeforePostingCredentials()
    {
        var postCount = 0;
        var provider = MakeProvider(request =>
        {
            if (request.Method == HttpMethod.Post) postCount++;
            return Html("<form method='post' action='https://attacker.invalid/collect'><input name='username'><input type='password' name='password'><input name='login' type='submit' value='Sign in'></form>");
        });

        var error = await Assert.ThrowsAsync<SportlinkLoginException>(() => provider.LoginAsync(Credentials()));
        Assert.Equal(SportlinkLoginFailure.UnsupportedChallenge, error.Failure);
        Assert.Equal(0, postCount);
        Assert.DoesNotContain("test-password", error.ToString());
    }

    [Fact]
    public async Task LoginAsync_StateMismatch_DoesNotExchangeCode()
    {
        var tokenRequests = 0;
        var provider = MakeProvider(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate'><input name='username'><input type='password' name='password'><input name='login' type='submit' value='Sign in'></form>");
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)) tokenRequests++;
            return Redirect("https://club.sportlink.com/dashboard?code=secret-code&state=attacker-value");
        });

        var error = await Assert.ThrowsAsync<SportlinkLoginException>(() => provider.LoginAsync(Credentials()));
        Assert.Equal(SportlinkLoginFailure.InvalidResponse, error.Failure);
        Assert.Equal(0, tokenRequests);
        Assert.DoesNotContain("secret-code", error.ToString());
    }

    [Fact]
    public async Task LoginAsync_UnknownOtpChallenge_FailsClosedWithoutRetrying()
    {
        var postCount = 0;
        var provider = MakeProvider(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate'><input name='username'><input type='password' name='password'><input name='login' type='submit' value='Sign in'></form>");
            postCount++;
            return Html("<script src='/resources/recaptcha/widget.js'></script><form method='post' action='/realms/sportlink/login-actions/authenticate'><input name='otp'><input type='submit' name='login' value='Sign in'></form>");
        });

        var error = await Assert.ThrowsAsync<SportlinkLoginException>(() => provider.LoginAsync(Credentials()));
        Assert.Equal(SportlinkLoginFailure.UnsupportedChallenge, error.Failure);
        Assert.Equal(1, postCount);
        Assert.DoesNotContain("test-password", error.ToString());
    }

    [Fact]
    public async Task LoginAsync_ExternalRedirect_FailsClosedWithoutFollowingIt()
    {
        var requestCount = 0;
        var provider = MakeProvider(request =>
        {
            requestCount++;
            if (request.Method == HttpMethod.Get)
                return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate'><input name='username'><input type='password' name='password'><input name='login' type='submit' value='Sign in'></form>");
            return Redirect("https://attacker.invalid/next?code=sensitive");
        });

        var error = await Assert.ThrowsAsync<SportlinkLoginException>(() => provider.LoginAsync(Credentials()));
        Assert.Equal(SportlinkLoginFailure.UnsupportedChallenge, error.Failure);
        Assert.Equal(2, requestCount);
        Assert.DoesNotContain("sensitive", error.ToString());
    }

    [Fact]
    public async Task LoginAsync_DuplicateHiddenParameter_FailsBeforePostingCredentials()
    {
        var postCount = 0;
        var provider = MakeProvider(request =>
        {
            if (request.Method == HttpMethod.Post) postCount++;
            return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate'><input type='hidden' name='session_code' value='one'><input type='hidden' name='session_code' value='two'><input name='username'><input type='password' name='password'><input type='submit' name='login' value='Sign in'></form>");
        });

        var error = await Assert.ThrowsAsync<SportlinkLoginException>(() => provider.LoginAsync(Credentials()));
        Assert.Equal(SportlinkLoginFailure.UnsupportedChallenge, error.Failure);
        Assert.Equal(0, postCount);
    }

    [Fact]
    public async Task LoginAsync_LoginPageReturnedAfterCredentials_StopsWithoutSecondCredentialAttempt()
    {
        var postCount = 0;
        var provider = MakeProvider(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate'><input name='username'><input type='password' name='password'><input type='submit' name='login' value='Sign in'></form>");
            postCount++;
            return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate'><span class='alert-error'>Invalid username or password</span><input name='username'><input type='password' name='password'><input type='submit' name='login' value='Sign in'></form>");
        });

        var error = await Assert.ThrowsAsync<SportlinkLoginException>(() => provider.LoginAsync(Credentials()));
        Assert.Equal(SportlinkLoginFailure.InvalidCredentials, error.Failure);
        Assert.Equal(1, postCount);
    }

    [Fact]
    public async Task LoginAsync_NonAuthenticateAction_FailsBeforePostingCredentials()
    {
        var postCount = 0;
        var provider = MakeProvider(request =>
        {
            if (request.Method == HttpMethod.Post) postCount++;
            return Html("<form method='post' action='/realms/sportlink/login-actions/required-action'><input name='username'><input type='password' name='password'><input type='submit' name='login' value='Sign in'></form>");
        });

        var error = await Assert.ThrowsAsync<SportlinkLoginException>(() => provider.LoginAsync(Credentials()));
        Assert.Equal(SportlinkLoginFailure.UnsupportedChallenge, error.Failure);
        Assert.Equal(0, postCount);
    }

    [Fact]
    public async Task LoginAsync_CallbackWithUserInfo_FailsBeforeCodeExchange()
    {
        var tokenRequests = 0;
        var initial = string.Empty;
        var provider = MakeProvider(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                initial = request.RequestUri!.Query;
                return Html("<form method='post' action='/realms/sportlink/login-actions/authenticate'><input name='username'><input type='password' name='password'><input type='submit' name='login' value='Sign in'></form>");
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)) tokenRequests++;
            var state = HttpUtility.ParseQueryString(initial)["state"]!;
            return Redirect($"https://userinfo@club.sportlink.com/dashboard?code=fake&state={Uri.EscapeDataString(state)}");
        });

        var error = await Assert.ThrowsAsync<SportlinkLoginException>(() => provider.LoginAsync(Credentials()));
        Assert.Equal(SportlinkLoginFailure.UnsupportedChallenge, error.Failure);
        Assert.Equal(0, tokenRequests);
    }

    private static SportlinkAutoLoginProvider MakeProvider(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new(_ => new DelegateHandler(reply), new FixedTimeProvider(Now));

    private static SportlinkLoginCredentials Credentials() => new()
    {
        Username = "test-user", Password = "test-password", TotpSecret = "JBSWY3DPEHPK3PXP"
    };

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location) }
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
