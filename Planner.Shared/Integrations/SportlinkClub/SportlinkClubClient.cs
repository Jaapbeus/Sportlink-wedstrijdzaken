using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// Read-only HTTP client voor Sportlink Club API.
/// Handelt token-vernieuwing per functionele rol af, met in-memory caching en per-rol locking.
/// </summary>
public class SportlinkClubClient : ISportlinkClubClient
{
    private const string TokenEndpoint = "https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token";
    private const string MatchEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/Match";
    private const string ClientId = "sportlink-club-web";
    private const int TokenExpiryMarginSeconds = 60;

    private readonly HttpClient _httpClient;
    private readonly ISportlinkClubTokenStore _tokenStore;
    private readonly ILogger<SportlinkClubClient> _logger;

    // Per-role in-memory cache met access token + expiry
    private record CachedRoleToken(string AccessToken, DateTimeOffset ExpiresAtUtc, string HuidigRefreshToken);

    private readonly ConcurrentDictionary<string, CachedRoleToken> _tokenCache
        = new(StringComparer.OrdinalIgnoreCase);

    // Per-rol SemaphoreSlim om gelijktijdige refresh-aanroepen te serialiseren
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _rolSemaphores
        = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public SportlinkClubClient(HttpClient httpClient, ISportlinkClubTokenStore tokenStore, ILogger<SportlinkClubClient> logger)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _logger = logger;
    }

    public async Task<SportlinkClubResponse<SportlinkMatch>> GetMatchAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
    {
        // Haal/ververs access token voor deze rol
        var tokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken);
        if (tokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<SportlinkMatch>(tokenResult.Status, null, tokenResult.FoutmeldingVoorLog, null);

        var accessToken = tokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.SportlinkFout,
                null,
                "Access token is leeg na vernieuwing",
                null);

        // Eerst proberen met gecachte/vernieuwde token
        var response = await FetchMatchAsync(publicMatchId, accessToken, functioneleRol, cancellationToken);

        // Succes of fout die niet 401 is? Retourneer direct
        if (response.Status == SportlinkClubCallStatus.Ok || response.HttpStatusCode != 401)
            return response;

        // 401 ondanks geforceerde refresh → cache ongeldig maken en één keer opnieuw proberen
        _logger.LogInformation("401 ontvangen voor rol '{Rol}', cache wordt ongeldig gemaakt en opnieuw geprobeerd", functioneleRol);
        InvalidateTokenCache(functioneleRol);

        var retryTokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken, forceRefresh: true);
        if (retryTokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<SportlinkMatch>(retryTokenResult.Status, null, retryTokenResult.FoutmeldingVoorLog, null);

        var retryAccessToken = retryTokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(retryAccessToken))
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.SportlinkFout,
                null,
                "Access token is leeg na hernieuwing",
                null);

        // Tweede poging — als dit ook 401 geeft, dan is herkoppeling vereist
        var retryResponse = await FetchMatchAsync(publicMatchId, retryAccessToken, functioneleRol, cancellationToken);
        if (retryResponse.HttpStatusCode == 401)
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.HerkoppelingVereist,
                null,
                "Refresh token is ongeldig (401 blijft terugkomen). Rol moet opnieuw gekoppeld worden.",
                401);

        return retryResponse;
    }

    private async Task<(SportlinkClubCallStatus Status, string? AccessToken, string? FoutmeldingVoorLog)> RefreshTokenIfNeededAsync(
        string functioneleRol,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var now = DateTimeOffset.UtcNow;

        // Check cache — is accessToken nog geldig?
        if (!forceRefresh && _tokenCache.TryGetValue(functioneleRol, out var cached))
        {
            if (now.AddSeconds(TokenExpiryMarginSeconds) < cached.ExpiresAtUtc)
            {
                _logger.LogDebug("Access token voor rol '{Rol}' nog geldig, hergebruik uit cache", functioneleRol);
                return (SportlinkClubCallStatus.Ok, cached.AccessToken, null);
            }

            _logger.LogDebug("Access token voor rol '{Rol}' vervallen, verversen nodig", functioneleRol);
        }

        // Serialize per-rol vernieuwing
        var semaphore = _rolSemaphores.GetOrAdd(functioneleRol, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            // Double-check: mis tussendoor iemand anders al vernieuwd?
            if (!forceRefresh && _tokenCache.TryGetValue(functioneleRol, out var recheck))
            {
                if (now.AddSeconds(TokenExpiryMarginSeconds) < recheck.ExpiresAtUtc)
                    return (SportlinkClubCallStatus.Ok, recheck.AccessToken, null);
            }

            // Lees huiconstante refresh token
            var refreshToken = _tokenStore.LeesRefreshToken(functioneleRol);
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogWarning("Geen refresh token gekoppeld voor rol '{Rol}'", functioneleRol);
                return (SportlinkClubCallStatus.RolNietGekoppeld, null, $"Rol '{functioneleRol}' is niet gekoppeld aan Sportlink");
            }

            // Call refresh endpoint
            var refreshResult = await CallTokenEndpointAsync(refreshToken, cancellationToken);
            if (refreshResult.Status != SportlinkClubCallStatus.Ok)
                return (refreshResult.Status, refreshResult.AccessToken, refreshResult.FoutmeldingVoorLog);

            if (string.IsNullOrWhiteSpace(refreshResult.AccessToken) || !refreshResult.ExpiresIn.HasValue)
                return (SportlinkClubCallStatus.SportlinkFout, null, "Token endpoint gaf onvolledig antwoord");

            // Cache bijwerken
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshResult.ExpiresIn.Value);
            var newToken = new CachedRoleToken(refreshResult.AccessToken, expiresAt, refreshResult.NewRefreshToken ?? refreshToken);
            _tokenCache[functioneleRol] = newToken;

            // Async: schrijf token terug (niet-blocking)
            _ = Task.Run(async () =>
            {
                if (!string.IsNullOrWhiteSpace(refreshResult.NewRefreshToken))
                {
                    await _tokenStore.SchrijfRefreshTokenAsync(functioneleRol, refreshResult.NewRefreshToken, cancellationToken);
                }
            }, cancellationToken);

            return (SportlinkClubCallStatus.Ok, refreshResult.AccessToken, null);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private record TokenEndpointResult(
        SportlinkClubCallStatus Status,
        string? AccessToken,
        int? ExpiresIn,
        string? NewRefreshToken,
        string? FoutmeldingVoorLog);

    private async Task<TokenEndpointResult> CallTokenEndpointAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "client_id", ClientId },
                { "refresh_token", refreshToken }
            });

            var response = await _httpClient.PostAsync(TokenEndpoint, body, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && responseBody.Contains("invalid_grant"))
                {
                    _logger.LogWarning("Refresh token ongeldig (invalid_grant van token endpoint)");
                    return new TokenEndpointResult(SportlinkClubCallStatus.HerkoppelingVereist, null, null, null, "Refresh token is ongeldig");
                }

                _logger.LogWarning("Token endpoint fout: {StatusCode}", response.StatusCode);
                return new TokenEndpointResult(
                    SportlinkClubCallStatus.SportlinkFout,
                    null,
                    null,
                    null,
                    $"Token endpoint gaf {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var tokenResponse = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
            if (!tokenResponse.TryGetProperty("access_token", out var accessTokenElement))
                return new TokenEndpointResult(SportlinkClubCallStatus.SportlinkFout, null, null, null, "access_token ontbreekt in response");

            string? newRefreshToken = null;
            if (tokenResponse.TryGetProperty("refresh_token", out var refreshTokenElement))
                newRefreshToken = refreshTokenElement.GetString();

            var expiresIn = 3600; // default
            if (tokenResponse.TryGetProperty("expires_in", out var expiresInElement) && expiresInElement.TryGetInt32(out var ei))
                expiresIn = ei;

            return new TokenEndpointResult(
                SportlinkClubCallStatus.Ok,
                accessTokenElement.GetString(),
                expiresIn,
                newRefreshToken,
                null);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Token endpoint timeout");
            return new TokenEndpointResult(SportlinkClubCallStatus.NetwerkFout, null, null, null, "Timeout bij token endpoint");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Token endpoint netwerk fout");
            return new TokenEndpointResult(SportlinkClubCallStatus.NetwerkFout, null, null, null, "Netwerk fout bij token endpoint");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij token endpoint");
            return new TokenEndpointResult(SportlinkClubCallStatus.SportlinkFout, null, null, null, "Onverwachte fout bij token endpoint");
        }
    }

    private async Task<SportlinkClubResponse<SportlinkMatch>> FetchMatchAsync(
        string publicMatchId,
        string accessToken,
        string functioneleRol,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{MatchEndpoint}?PublicMatchId={Uri.EscapeDataString(publicMatchId)}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("X-Navajo-Entity", "competition/match/Match");
            request.Headers.Add("X-Navajo-Instance", "KNVB");
            request.Headers.Add("X-Navajo-Locale", "nl");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    var match = JsonSerializer.Deserialize<SportlinkMatch>(json, JsonOptions);
                    if (match == null)
                        return new SportlinkClubResponse<SportlinkMatch>(
                            SportlinkClubCallStatus.SportlinkFout,
                            null,
                            "Match data onvolledig in respons",
                            (int)response.StatusCode);

                    return new SportlinkClubResponse<SportlinkMatch>(
                        SportlinkClubCallStatus.Ok,
                        match,
                        null,
                        (int)response.StatusCode);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "JSON deserialisatie fout voor match endpoint");
                    return new SportlinkClubResponse<SportlinkMatch>(
                        SportlinkClubCallStatus.SportlinkFout,
                        null,
                        "JSON deserialisatie fout",
                        (int)response.StatusCode);
                }
            }

            // Niet succesvol
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<SportlinkMatch>(
                    SportlinkClubCallStatus.SportlinkFout, // Handled separately in GetMatchAsync
                    null,
                    "Unauthorized bij match endpoint",
                    401);

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Match endpoint gaf {StatusCode}: {Body}", response.StatusCode, errorBody);
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.SportlinkFout,
                null,
                $"Match endpoint gaf {response.StatusCode}",
                (int)response.StatusCode);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Match endpoint timeout voor publicMatchId '{PublicMatchId}'", publicMatchId);
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.NetwerkFout,
                null,
                "Timeout bij match endpoint",
                null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Match endpoint netwerk fout");
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.NetwerkFout,
                null,
                "Netwerk fout bij match endpoint",
                null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij match endpoint");
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.SportlinkFout,
                null,
                "Onverwachte fout bij match endpoint",
                null);
        }
    }

    private void InvalidateTokenCache(string functioneleRol)
    {
        _tokenCache.TryRemove(functioneleRol, out _);
    }
}
