using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Tokenbeheer per functionele rol (refresh, cache, validatie) (#1493) — zelfde klasse, alleen verplaatst. Geen gedragswijziging.</summary>
public partial class SportlinkClubClient
{
    private async Task<(SportlinkClubCallStatus Status, string? AccessToken, string? FoutmeldingVoorLog)> RefreshTokenIfNeededAsync(
        string functioneleRol,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var now = DateTimeOffset.UtcNow;

        // Check cache — is token nog geldig?
        if (_autoLogin is null && !forceRefresh && _tokenCache.TryGetValue(functioneleRol, out var cached))
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
            if (_autoLogin is null && !forceRefresh && _tokenCache.TryGetValue(functioneleRol, out var recheck))
            {
                if (now.AddSeconds(TokenExpiryMarginSeconds) < recheck.ExpiresAtUtc)
                    return (SportlinkClubCallStatus.Ok, recheck.AccessToken, null);
            }

            // #1411: dezelfde databaselease voor herlogin, refresh-rotatie en beheerwrites.
            await using var lease = _autoLogin is null ? null :
                await _autoLogin.AcquireLeaseAsync(functioneleRol, cancellationToken);
            if (_autoLogin is not null)
            {
                var login = await _autoLogin.TryLoginAsync(functioneleRol, false, cancellationToken);
                if (login is not null) return CacheLogin(functioneleRol, login);
            }

            // Lees het laatst duurzaam opgeslagen refresh-token onder de lease.
            var refreshToken = _tokenStore.LeesRefreshToken(functioneleRol);
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogWarning("Geen refresh token gekoppeld voor rol '{Rol}'", functioneleRol);
                return (SportlinkClubCallStatus.RolNietGekoppeld, null, $"Rol '{functioneleRol}' is niet gekoppeld aan Sportlink");
            }

            // Call refresh endpoint. #1387: dit token-refreshpad ligt vóór ELKE andere Sportlink-
            // aanroep (ExecuteWithTokenRetryAsync roept dit altijd eerst aan) — zonder eigen retry
            // hier was één enkele trage/mislukte tokenverversing genoeg om alles daarachter te laten
            // falen, terwijl elke andere aanroep al wel een transiënte retry kreeg.
            var refreshResult = await CallTokenEndpointAsync(refreshToken, cancellationToken);
            if (refreshResult.Status == SportlinkClubCallStatus.NetwerkFout)
            {
                await WachtVoorTransienteRetryAsync($"token-endpoint, rol '{functioneleRol}'", cancellationToken);
                refreshResult = await CallTokenEndpointAsync(refreshToken, cancellationToken);
            }
            if (refreshResult.Status == SportlinkClubCallStatus.HerkoppelingVereist && _autoLogin is not null)
            {
                var login = await _autoLogin.TryLoginAsync(functioneleRol, true, cancellationToken);
                if (login is not null) return CacheLogin(functioneleRol, login);
            }
            if (refreshResult.Status != SportlinkClubCallStatus.Ok)
                return (refreshResult.Status, refreshResult.AccessToken, refreshResult.FoutmeldingVoorLog);

            if (string.IsNullOrWhiteSpace(refreshResult.AccessToken) || !refreshResult.ExpiresIn.HasValue)
                return (SportlinkClubCallStatus.SportlinkFout, null, "Token endpoint gaf onvolledig antwoord");

            // #1411: opslag afwachten vóór caching/succes; geen fire-and-forget credentialrotatie.
            if (!string.IsNullOrWhiteSpace(refreshResult.NewRefreshToken))
                await _tokenStore.SchrijfRefreshTokenAsync(functioneleRol, refreshResult.NewRefreshToken, cancellationToken);
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshResult.ExpiresIn.Value);
            _tokenCache[functioneleRol] = new CachedRoleToken(refreshResult.AccessToken,
                expiresAt, refreshResult.NewRefreshToken ?? refreshToken);

            return (SportlinkClubCallStatus.Ok, refreshResult.AccessToken, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SportlinkLoginException)
        {
            _tokenCache.TryRemove(functioneleRol, out _);
            _logger.LogWarning("Automatische Sportlink-login niet voltooid; controleer de beheerstatus.");
            return (SportlinkClubCallStatus.HerkoppelingVereist, null, "Automatisch aanmelden niet voltooid");
        }
        catch (Exception)
        {
            _tokenCache.TryRemove(functioneleRol, out _);
            _logger.LogWarning("Sportlink-token kon niet veilig worden vernieuwd of opgeslagen.");
            return (SportlinkClubCallStatus.SportlinkFout, null, "Veilige tokenvernieuwing niet beschikbaar");
        }
        finally
        {
            semaphore.Release();
        }
    }

    private (SportlinkClubCallStatus Status, string? AccessToken, string? FoutmeldingVoorLog)
        CacheLogin(string role, SportlinkLoginResult login)
    {
        _tokenCache[role] = new CachedRoleToken(login.AccessToken,
            DateTimeOffset.UtcNow.AddSeconds(login.ExpiresInSeconds), login.RefreshToken);
        return (SportlinkClubCallStatus.Ok, login.AccessToken, null);
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

            var response = await VerstuurMetTimeoutAsync(ct => _httpClient.PostAsync(TokenEndpoint, body, ct), cancellationToken);
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
    /// <summary>
    /// Eén refresh_token-grant bij Keycloak om een zojuist aangeleverd refresh-token te valideren
    /// vóór opslag (#991; hier gecentraliseerd bij #1122 zodat de tokenregistratie geen eigen kopie
    /// van endpoint en client-id meer draagt). Statisch en zonder tokenstore: dit token is nog van
    /// niemand. Logt niets — de aanroeper kent alleen waar/niet waar.
    /// <para>
    /// #1387: een timeout/netwerkfout hier gaf vóór deze fix een onafgevangen exception (500 in de
    /// aanroepende Function-endpoint) — geen retry (dit is al een expliciete, eenmalige
    /// gebruikersactie: "opnieuw registreren"), maar wel <c>false</c> in plaats van een crash, zodat
    /// de aanroeper hetzelfde 409-antwoord geeft als bij een echt geweigerd token.
    /// </para>
    /// </summary>
    public static async Task<bool> ValideerRefreshTokenAsync(HttpClient http, string refreshToken, CancellationToken cancellationToken = default)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        });
        try
        {
            using var response = await http.PostAsync(TokenEndpoint, body, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private void InvalidateTokenCache(string functioneleRol)
    {
        _tokenCache.TryRemove(functioneleRol, out _);
    }
}
