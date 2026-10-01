using AngleSharp.Html.Parser;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Performs the narrowly supported Sportlink username/password + TOTP authorization-code flow.
/// A new cookie jar and handler are created for every call; redirects are inspected manually.
/// </summary>
public sealed class SportlinkAutoLoginProvider : ISportlinkAutoLoginProvider
{
    private static readonly Uri AuthorizationEndpoint = new("https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/auth");
    private static readonly Uri TokenEndpoint = new("https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token");
    private static readonly Uri RedirectUri = new("https://club.sportlink.com/dashboard");
    private const string ClientId = "sportlink-club-web";
    private const int MaxBodyBytes = 512 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<CookieContainer, HttpMessageHandler> _handlerFactory;
    private readonly TimeProvider _timeProvider;

    public SportlinkAutoLoginProvider()
        : this(static cookies => new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        }, TimeProvider.System)
    {
    }

    internal SportlinkAutoLoginProvider(
        Func<CookieContainer, HttpMessageHandler> handlerFactory,
        TimeProvider timeProvider)
    {
        _handlerFactory = handlerFactory;
        _timeProvider = timeProvider;
    }

    public async Task<SportlinkLoginResult> LoginAsync(
        SportlinkLoginCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrEmpty(credentials.Password) ||
            string.IsNullOrWhiteSpace(credentials.TotpSecret))
            throw new SportlinkLoginException(SportlinkLoginFailure.InvalidCredentials);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var ct = timeout.Token;
        var cookies = new CookieContainer();
        using var handler = _handlerFactory(cookies);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        try
        {
            var state = RandomToken(32);
            var verifier = RandomToken(48);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var authorize = new UriBuilder(AuthorizationEndpoint)
            {
                Query = FormEncode(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["redirect_uri"] = RedirectUri.AbsoluteUri,
                    ["response_type"] = "code",
                    ["scope"] = "openid profile email",
                    ["state"] = state,
                    ["code_challenge"] = challenge,
                    ["code_challenge_method"] = "S256"
                })
            }.Uri;

            using var initial = await SendAsync(client, HttpMethod.Get, authorize, null, ct);
            var callback = await GetCallbackAsync(initial, state, ct);
            if (callback is null)
            {
                var form = await ReadFormAsync(initial, ct);
                var loginInputs = GetLoginInputs(form);
                var fields = new Dictionary<string, string>(form.SubmissionFields, StringComparer.Ordinal)
                {
                    [loginInputs.Username] = credentials.Username,
                    [loginInputs.Password] = credentials.Password
                };
                using var submitted = await SendAsync(client, HttpMethod.Post, form.Action,
                    new FormUrlEncodedContent(fields), ct);
                callback = await GetCallbackAsync(submitted, state, ct);
                if (callback is null)
                {
                    var otpForm = await ReadFormAsync(submitted, ct);
                    if (otpForm.Element.QuerySelector("input[type=password]") is not null)
                        throw new SportlinkLoginException(SportlinkLoginFailure.InvalidCredentials);
                    var otpField = GetOtpField(otpForm);
                    var otp = SportlinkTotp.Generate(credentials.TotpSecret, _timeProvider.GetUtcNow(),
                        credentials.TotpAlgorithm, credentials.TotpDigits, credentials.TotpPeriodSeconds);
                    var otpFields = new Dictionary<string, string>(otpForm.SubmissionFields, StringComparer.Ordinal)
                    {
                        [otpField] = otp
                    };
                    using var otpSubmitted = await SendAsync(client, HttpMethod.Post, otpForm.Action,
                        new FormUrlEncodedContent(otpFields), ct);
                    callback = await GetCallbackAsync(otpSubmitted, state, ct);
                    if (callback is null)
                        throw new SportlinkLoginException(SportlinkLoginFailure.InvalidCredentials);
                }
            }

            return await ExchangeCodeAsync(client, callback, verifier, ct);
        }
        catch (SportlinkLoginException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SportlinkLoginException(SportlinkLoginFailure.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Never expose HTTP exception text, response bodies, URI query strings, or credentials.
            throw new SportlinkLoginException(SportlinkLoginFailure.NetworkFailure);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, Uri uri, HttpContent? content, CancellationToken ct)
    {
        EnsureAllowedIdentityUri(uri);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // Reject redirects to anywhere except a validated OAuth callback. No redirect is followed.
        if (IsRedirect(response.StatusCode))
        {
            var location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                throw new SportlinkLoginException(SportlinkLoginFailure.InvalidResponse);
            }
        }
        return response;
    }

    private static async Task<Uri?> GetCallbackAsync(HttpResponseMessage response, string expectedState, CancellationToken ct)
    {
        if (!IsRedirect(response.StatusCode)) return null;
        var location = response.Headers.Location!;
        var callback = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage!.RequestUri!, location);
        if (callback.Scheme != Uri.UriSchemeHttps || callback.UserInfo.Length != 0 || callback.Host != RedirectUri.Host ||
            callback.Port != RedirectUri.Port || callback.AbsolutePath != RedirectUri.AbsolutePath ||
            !string.IsNullOrEmpty(callback.Fragment))
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);

        var query = ParseQuery(callback.Query);
        if (query.TryGetValue("error", out _))
            throw new SportlinkLoginException(SportlinkLoginFailure.InvalidCredentials);
        if (!query.TryGetValue("state", out var state) || !FixedEquals(state, expectedState) ||
            !query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new SportlinkLoginException(SportlinkLoginFailure.InvalidResponse);
        if (query.TryGetValue("iss", out var issuer) && issuer != "https://idm.sportlink.com/realms/sportlink")
            throw new SportlinkLoginException(SportlinkLoginFailure.InvalidResponse);
        return new Uri("https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/auth?code=" + Uri.EscapeDataString(code));
    }

    private static async Task<SportlinkLoginResult> ExchangeCodeAsync(
        HttpClient client, Uri callback, string verifier, CancellationToken ct)
    {
        var code = ParseQuery(callback.Query)["code"];
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri.AbsoluteUri,
            ["code_verifier"] = verifier
        };
        using var response = await SendAsync(client, HttpMethod.Post, TokenEndpoint, new FormUrlEncodedContent(fields), ct);
        if (!response.IsSuccessStatusCode) throw new SportlinkLoginException(SportlinkLoginFailure.InvalidCredentials);
        var bytes = await ReadBodyAsync(response, ct);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        var access = RequiredString(root, "access_token");
        var refresh = RequiredString(root, "refresh_token");
        var expires = RequiredInt(root, "expires_in");
        var refreshExpires = root.TryGetProperty("refresh_expires_in", out var refreshExpiry) && refreshExpiry.TryGetInt32(out var value)
            ? value : 0;
        return new SportlinkLoginResult(access, refresh, expires, refreshExpires);
    }

    private static async Task<LoginForm> ReadFormAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new SportlinkLoginException(SportlinkLoginFailure.InvalidCredentials);
        var bytes = await ReadBodyAsync(response, ct);
        var document = new HtmlParser().ParseDocument(Encoding.UTF8.GetString(bytes));
        if (document.QuerySelectorAll("form").Length != 1 || ContainsUnsupportedChallenge(document))
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
        var form = document.QuerySelector("form")!;
        if (!string.Equals(form.GetAttribute("method") ?? "get", "post", StringComparison.OrdinalIgnoreCase))
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
        if (form.QuerySelector("select, textarea") is not null)
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
        var baseUri = response.RequestMessage?.RequestUri ?? AuthorizationEndpoint;
        var actionValue = form.GetAttribute("action");
        var action = new Uri(baseUri, string.IsNullOrWhiteSpace(actionValue) ? baseUri.PathAndQuery : actionValue);
        EnsureAllowedFormAction(action);
        var submissionFields = CollectSubmissionFields(form);
        return new LoginForm(action, submissionFields, form);
    }

    private static Dictionary<string, string> CollectSubmissionFields(AngleSharp.Dom.IElement form)
    {
        var submissionFields = new Dictionary<string, string>(StringComparer.Ordinal);
        var submitCount = 0;
        foreach (var input in form.QuerySelectorAll("input"))
        {
            var name = input.GetAttribute("name");
            var type = (input.GetAttribute("type") ?? "text").ToLowerInvariant();
            switch (type)
            {
                case "hidden":
                    AddUniqueField(submissionFields, name, input.GetAttribute("value") ?? string.Empty);
                    break;
                case "submit":
                    if (name != "login") throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
                    AddUniqueField(submissionFields, name, input.GetAttribute("value") ?? string.Empty);
                    submitCount++;
                    break;
                case "checkbox":
                    if (name != "rememberMe") throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
                    if (input.HasAttribute("checked")) AddUniqueField(submissionFields, name, input.GetAttribute("value") ?? "on");
                    break;
                case "text":
                case "email":
                case "password":
                case "tel":
                case "number":
                    break;
                default:
                    throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
            }
        }
        foreach (var button in form.QuerySelectorAll("button"))
        {
            var type = (button.GetAttribute("type") ?? "submit").ToLowerInvariant();
            if (type == "button" &&
                (button.HasAttribute("data-password-toggle") || button.GetAttribute("aria-controls") == "password"))
                continue;

            // Keycloak currently renders its login action as a button (name=login, no value),
            // while older/form-fixture pages use input[type=submit]. In HTML the button's
            // displayed text is not a submitted value, so preserve the actual empty value.
            if (type == "submit" && button.GetAttribute("name") == "login")
            {
                AddUniqueField(submissionFields, "login", button.GetAttribute("value") ?? string.Empty);
                submitCount++;
                continue;
            }

            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
        }
        if (submitCount != 1)
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
        return submissionFields;
    }

    private static void AddUniqueField(Dictionary<string, string> fields, string? name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || !fields.TryAdd(name, value))
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
    }

    private static (string Username, string Password) GetLoginInputs(LoginForm form)
    {
        var inputs = form.Element.QuerySelectorAll("input");
        var passwords = inputs.Where(static i => string.Equals(i.GetAttribute("type"), "password", StringComparison.OrdinalIgnoreCase)).ToArray();
        var users = inputs.Where(static i =>
        {
            var type = i.GetAttribute("type");
            var name = i.GetAttribute("name");
            return (type is null or "text" or "email") && (name is "username" or "email" or "login");
        }).ToArray();
        var credentialInputs = inputs.Where(static i => (i.GetAttribute("type") ?? "text").ToLowerInvariant() is "text" or "email" or "password" or "tel" or "number").ToArray();
        if (passwords.Length != 1 || users.Length != 1 || credentialInputs.Length != 2)
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
        return (users[0].GetAttribute("name")!, passwords[0].GetAttribute("name")!);
    }

    private static string GetOtpField(LoginForm form)
    {
        var candidates = form.Element.QuerySelectorAll("input").Where(static i =>
        {
            var name = i.GetAttribute("name");
            var autocomplete = i.GetAttribute("autocomplete");
            return string.Equals(autocomplete, "one-time-code", StringComparison.OrdinalIgnoreCase) ||
                name is "otp" or "totp" or "code" or "credential";
        }).ToArray();
        var containsSecretField = form.Element.QuerySelectorAll("input[type=password]").Length > 0;
        var credentialInputs = form.Element.QuerySelectorAll("input").Where(static i => (i.GetAttribute("type") ?? "text").ToLowerInvariant() is "text" or "email" or "password" or "tel" or "number").ToArray();
        if (containsSecretField || candidates.Length != 1 || credentialInputs.Length != 1 || candidates[0].GetAttribute("type") is not (null or "text" or "tel" or "number"))
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
        return candidates[0].GetAttribute("name")!;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxBodyBytes)
            throw new SportlinkLoginException(SportlinkLoginFailure.InvalidResponse);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > MaxBodyBytes)
                throw new SportlinkLoginException(SportlinkLoginFailure.InvalidResponse);
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static void EnsureAllowedIdentityUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "idm.sportlink.com" ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !IsRealmPath(uri.AbsolutePath))
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
    }

    private static void EnsureAllowedFormAction(Uri uri)
    {
        EnsureAllowedIdentityUri(uri);
        if (uri.AbsolutePath != "/realms/sportlink/login-actions/authenticate")
            throw new SportlinkLoginException(SportlinkLoginFailure.UnsupportedChallenge);
    }

    private static bool IsRealmPath(string path) =>
        path == "/realms/sportlink" || path == "/realms/sportlink/login-actions/authenticate" ||
        path == "/realms/sportlink/protocol/openid-connect/auth" ||
        path == "/realms/sportlink/protocol/openid-connect/token";

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            if (!result.TryAdd(key, value)) throw new SportlinkLoginException(SportlinkLoginFailure.InvalidResponse);
        }
        return result;
    }

    private static string FormEncode(Dictionary<string, string> fields) =>
        string.Join("&", fields.Select(static p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.Moved or HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new SportlinkLoginException(SportlinkLoginFailure.InvalidResponse);

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number >= 0
            ? number : throw new SportlinkLoginException(SportlinkLoginFailure.InvalidResponse);

    private static bool ContainsUnsupportedChallenge(AngleSharp.Dom.IDocument document)
    {
        if (document.QuerySelector("iframe") is not null || document.QuerySelector("a[id^='social-'], a[href*='/broker/']") is not null ||
            document.QuerySelector("[data-sitekey]") is not null)
            return true;
        foreach (var element in document.QuerySelectorAll("script, [id], [class], [src], [title]"))
        {
            var marker = string.Join(" ", element.GetAttribute("id"), element.GetAttribute("class"),
                element.GetAttribute("src"), element.GetAttribute("title"));
            if (marker.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
                marker.Contains("recaptcha", StringComparison.OrdinalIgnoreCase) ||
                marker.Contains("hcaptcha", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private sealed record LoginForm(Uri Action, Dictionary<string, string> SubmissionFields, AngleSharp.Dom.IElement Element);
}
