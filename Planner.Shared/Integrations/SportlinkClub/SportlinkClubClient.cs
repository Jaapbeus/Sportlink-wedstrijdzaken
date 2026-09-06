using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// HTTP client voor Sportlink Club API. Handelt token-vernieuwing per functionele rol af, met
/// in-memory caching en per-rol locking. Sinds #992 ook schrijvende aanroepen (nog altijd puur
/// transport — guardrails/audit horen bij de aanroeper, zie <see cref="SportlinkMutationGuard"/>).
/// </summary>
public class SportlinkClubClient : ISportlinkClubClient
{
    private const string TokenEndpoint = "https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token";
    private const string MatchEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/Match";
    private const string MatchProgramOverviewEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/MatchProgramOverview";
    private const string UpdateMatchDressingRoomsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/UpdateMatchDressingRooms";
    private const string UpdateMatchFieldEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/UpdateMatchField";
    private const string MatchChangeRequestsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/changerequest/MatchChangeRequests";
    private const string MatchChangeRequestActionEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/changerequest/MatchChangeRequestAction";
    private const string UserInfoEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/user/UserInfo";
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

        var token = tokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.SportlinkFout,
                null,
                "Access token is leeg na vernieuwing",
                null);

        // Eerst proberen met gecachte/vernieuwde token
        var response = await FetchMatchAsync(publicMatchId, token, functioneleRol, cancellationToken);

        // Succes of fout die niet 401 is? Retourneer direct
        if (response.Status == SportlinkClubCallStatus.Ok || response.HttpStatusCode != 401)
            return response;

        // 401 ondanks geforceerde refresh → cache ongeldig maken en één keer opnieuw proberen
        _logger.LogInformation("401 ontvangen voor rol '{Rol}', cache wordt ongeldig gemaakt en opnieuw geprobeerd", functioneleRol);
        InvalidateTokenCache(functioneleRol);

        var retryTokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken, forceRefresh: true);
        if (retryTokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<SportlinkMatch>(retryTokenResult.Status, null, retryTokenResult.FoutmeldingVoorLog, null);

        var retryToken = retryTokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(retryToken))
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.SportlinkFout,
                null,
                "Access token is leeg na hernieuwing",
                null);

        // Tweede poging — als dit ook 401 geeft, dan is herkoppeling vereist
        var retryResponse = await FetchMatchAsync(publicMatchId, retryToken, functioneleRol, cancellationToken);
        if (retryResponse.HttpStatusCode == 401)
            return new SportlinkClubResponse<SportlinkMatch>(
                SportlinkClubCallStatus.HerkoppelingVereist,
                null,
                "Refresh token is ongeldig (401 blijft terugkomen). Rol moet opnieuw gekoppeld worden.",
                401);

        return retryResponse;
    }

    public async Task<SportlinkClubResponse<SportlinkMatchProgramEntry>> ResolvePublicMatchIdAsync(
        string functioneleRol,
        long wedstrijdnummer,
        DateOnly datum,
        CancellationToken cancellationToken = default)
    {
        var overview = await GetMatchProgramOverviewAsync(functioneleRol, datum, cancellationToken);
        if (overview.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<SportlinkMatchProgramEntry>(overview.Status, null, overview.FoutmeldingVoorLog, overview.HttpStatusCode);

        // Ok + Data=null: de aanroep zelf slaagde, deze wedstrijd stond er alleen niet in — geen
        // fout, zie XML-doc op ISportlinkClubClient.ResolvePublicMatchIdAsync.
        var gevonden = overview.Data?.FirstOrDefault(e => e.ExternalMatchId == wedstrijdnummer);
        return new SportlinkClubResponse<SportlinkMatchProgramEntry>(SportlinkClubCallStatus.Ok, gevonden, null, overview.HttpStatusCode);
    }

    /// <summary>
    /// Haalt het volledige, niet-club-gescoped wedstrijdprogramma van Sportlink op voor één dag
    /// (<c>MatchProgramOverview</c>, smal 1-daags bereik — zie onderzoeksrapport §2.2/§2.5).
    /// Losgetrokken van <see cref="ResolvePublicMatchIdAsync"/> zodat een aanroeper met meerdere
    /// eigen wedstrijden op dezelfde datum de trage (12+ s) aanroep maar ÉÉN keer per datum hoeft te
    /// doen in plaats van eenmaal per wedstrijd (zie <c>SportlinkPublicMatchIdWarmupTimerFunction</c>,
    /// epic #986 issue #1017).
    /// </summary>
    public async Task<SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>> GetMatchProgramOverviewAsync(
        string functioneleRol,
        DateOnly datum,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken);
        if (tokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(tokenResult.Status, null, tokenResult.FoutmeldingVoorLog, null);

        var token = tokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na vernieuwing", null);

        var response = await FetchMatchProgramOverviewRawAsync(datum, token, cancellationToken);

        // Zelfde 401-eenmalige-retry als GetMatchAsync.
        if (response.Status == SportlinkClubCallStatus.Ok || response.HttpStatusCode != 401)
            return response;

        InvalidateTokenCache(functioneleRol);
        var retryTokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken, forceRefresh: true);
        if (retryTokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(retryTokenResult.Status, null, retryTokenResult.FoutmeldingVoorLog, null);

        var retryToken = retryTokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(retryToken))
            return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na hernieuwing", null);

        var retryResponse = await FetchMatchProgramOverviewRawAsync(datum, retryToken, cancellationToken);
        if (retryResponse.HttpStatusCode == 401)
            return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                SportlinkClubCallStatus.HerkoppelingVereist,
                null,
                "Refresh token is ongeldig (401 blijft terugkomen). Rol moet opnieuw gekoppeld worden.",
                401);

        return retryResponse;
    }

    public async Task<SportlinkClubCallStatus> VerversTokenAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default)
    {
        // forceRefresh: true — een keep-alive moet de refresh_token-grant echt uitoefenen bij
        // Keycloak, niet stoppen bij een nog geldig geachte in-memory access-tokencache (die cache
        // bewijst niets over of de onderliggende refresh_token nog actief is bij Keycloak zelf).
        var result = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken, forceRefresh: true);
        if (result.Status != SportlinkClubCallStatus.Ok)
            _logger.LogWarning("Keep-alive-refresh voor rol '{Rol}' gaf status {Status}: {Fout}",
                functioneleRol, result.Status, result.FoutmeldingVoorLog);
        return result.Status;
    }

    public Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateDressingRoomsAsync(
        string functioneleRol,
        string publicMatchId,
        string? homeDressingRoomId,
        string? awayDressingRoomId,
        string? officialDressingRoomId,
        CancellationToken cancellationToken = default)
        => ExecuteMutationWithRetryAsync(
            functioneleRol,
            (token, ct) => PutDressingRoomsAsync(publicMatchId, homeDressingRoomId, awayDressingRoomId, officialDressingRoomId, token, ct),
            cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateFieldAsync(
        string functioneleRol,
        string publicMatchId,
        string? fieldId,
        string? fieldSize,
        int? fieldOffset,
        bool isForceUpdate,
        CancellationToken cancellationToken = default)
        => ExecuteMutationWithRetryAsync(
            functioneleRol,
            (token, ct) => PutFieldAsync(publicMatchId, fieldId, fieldSize, fieldOffset, isForceUpdate, token, ct),
            cancellationToken);

    public async Task<SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>> GetChangeRequestsAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken);
        if (tokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(tokenResult.Status, null, tokenResult.FoutmeldingVoorLog, null);

        var token = tokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na vernieuwing", null);

        var response = await FetchChangeRequestsAsync(token, cancellationToken);

        if (response.Status == SportlinkClubCallStatus.Ok || response.HttpStatusCode != 401)
            return response;

        InvalidateTokenCache(functioneleRol);
        var retryTokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken, forceRefresh: true);
        if (retryTokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(retryTokenResult.Status, null, retryTokenResult.FoutmeldingVoorLog, null);

        var retryToken = retryTokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(retryToken))
            return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na hernieuwing", null);

        var retryResponse = await FetchChangeRequestsAsync(retryToken, cancellationToken);
        if (retryResponse.HttpStatusCode == 401)
            return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(
                SportlinkClubCallStatus.HerkoppelingVereist, null,
                "Refresh token is ongeldig (401 blijft terugkomen). Rol moet opnieuw gekoppeld worden.", 401);

        return retryResponse;
    }

    private async Task<SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>> FetchChangeRequestsAsync(
        string token, CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, MatchChangeRequestsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Navajo-Entity", "competition/match/changerequest/MatchChangeRequests");
            request.Headers.Add("X-Navajo-Instance", "KNVB");
            request.Headers.Add("X-Navajo-Locale", "nl");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(
                    SportlinkClubCallStatus.SportlinkFout, null, "Unauthorized bij MatchChangeRequests endpoint", 401);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MatchChangeRequests endpoint gaf {StatusCode}", response.StatusCode);
                return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"MatchChangeRequests endpoint gaf {response.StatusCode}", (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            List<SportlinkChangeRequest>? items;
            try
            {
                // Responsvorm (kale array vs. genest) niet live bevestigd (issue #996) — zelfde
                // defensieve aanpak als FetchMatchProgramOverviewRawAsync.
                using var doc = JsonDocument.Parse(json);
                var element = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement
                    : doc.RootElement.TryGetProperty("ChangeRequests", out var cr) ? cr
                    : doc.RootElement.TryGetProperty("changeRequests", out var crLower) ? crLower
                    : doc.RootElement.TryGetProperty("Items", out var itemsProp) ? itemsProp
                    : default;

                if (element.ValueKind != JsonValueKind.Array)
                    return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(
                        SportlinkClubCallStatus.SportlinkFout, null, "MatchChangeRequests-respons had onverwachte vorm", (int)response.StatusCode);

                items = JsonSerializer.Deserialize<List<SportlinkChangeRequest>>(element.GetRawText(), JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "JSON deserialisatie fout voor MatchChangeRequests endpoint");
                return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(
                    SportlinkClubCallStatus.SportlinkFout, null, "JSON deserialisatie fout", (int)response.StatusCode);
            }

            return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(
                SportlinkClubCallStatus.Ok, items ?? new List<SportlinkChangeRequest>(), null, (int)response.StatusCode);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MatchChangeRequests endpoint timeout");
            return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(SportlinkClubCallStatus.NetwerkFout, null, "Timeout bij MatchChangeRequests endpoint", null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MatchChangeRequests endpoint netwerk fout");
            return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(SportlinkClubCallStatus.NetwerkFout, null, "Netwerk fout bij MatchChangeRequests endpoint", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij MatchChangeRequests endpoint");
            return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(SportlinkClubCallStatus.SportlinkFout, null, "Onverwachte fout bij MatchChangeRequests endpoint", null);
        }
    }

    public Task<SportlinkClubResponse<SportlinkMutationResult>> ActOnChangeRequestAsync(
        string functioneleRol,
        string actie,
        string publicMatchId,
        string publicRequestId,
        string? remarks,
        CancellationToken cancellationToken = default)
        => ExecuteMutationWithRetryAsync(
            functioneleRol,
            async (token, ct) =>
            {
                // PublicPersonId van de ingelogde (service-)gebruiker is verplicht in de
                // actie-body — niet apart gecached (naast de token-cache): dit endpoint wordt
                // zelden aangeroepen (een admin keurt af en toe een verzoek goed/af), dus één
                // extra GET per poging weegt niet op tegen nóg een cache-laag.
                var userInfo = await FetchUserInfoAsync(token, ct);
                if (userInfo.Status != SportlinkClubCallStatus.Ok || string.IsNullOrWhiteSpace(userInfo.Data?.PublicPersonId))
                    return new SportlinkClubResponse<SportlinkMutationResult>(
                        userInfo.Status == SportlinkClubCallStatus.Ok ? SportlinkClubCallStatus.SportlinkFout : userInfo.Status,
                        null, userInfo.FoutmeldingVoorLog ?? "Kon PublicPersonId niet ophalen via UserInfo", userInfo.HttpStatusCode);

                return await PutChangeRequestActionAsync(actie, publicMatchId, userInfo.Data.PublicPersonId, publicRequestId, remarks, token, ct);
            },
            cancellationToken);

    private async Task<SportlinkClubResponse<SportlinkUserInfo>> FetchUserInfoAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Navajo-Entity", "user/UserInfo");
            request.Headers.Add("X-Navajo-Instance", "KNVB");
            request.Headers.Add("X-Navajo-Locale", "nl");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<SportlinkUserInfo>(SportlinkClubCallStatus.SportlinkFout, null, "Unauthorized bij UserInfo endpoint", 401);

            if (!response.IsSuccessStatusCode)
                return new SportlinkClubResponse<SportlinkUserInfo>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"UserInfo endpoint gaf {response.StatusCode}", (int)response.StatusCode);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var info = JsonSerializer.Deserialize<SportlinkUserInfo>(json, JsonOptions);
            return new SportlinkClubResponse<SportlinkUserInfo>(SportlinkClubCallStatus.Ok, info, null, (int)response.StatusCode);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialisatie fout voor UserInfo endpoint");
            return new SportlinkClubResponse<SportlinkUserInfo>(SportlinkClubCallStatus.SportlinkFout, null, "JSON deserialisatie fout", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij UserInfo endpoint");
            return new SportlinkClubResponse<SportlinkUserInfo>(SportlinkClubCallStatus.NetwerkFout, null, "Netwerk fout bij UserInfo endpoint", null);
        }
    }

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutChangeRequestActionAsync(
        string actie, string publicMatchId, string publicPersonId, string publicRequestId, string? remarks,
        string token, CancellationToken cancellationToken)
    {
        var body = new
        {
            Action = actie,
            PublicMatchId = publicMatchId,
            PublicPersonId = publicPersonId,
            PublicRequestId = publicRequestId,
            Remarks = remarks
        };
        return PutMutationAsync(
            MatchChangeRequestActionEndpoint, "competition/match/changerequest/MatchChangeRequestAction", body, token, cancellationToken);
    }

    private sealed record SportlinkUserInfo(string? PublicPersonId);

    /// <summary>
    /// Gedeelde token-refresh/401-eenmalige-retry-wrapper voor alle schrijvende Sportlink-aanroepen
    /// (#992 kleedkamers, #993 veld, en toekomstige mutaties) — derde bijna-identieke kopie van
    /// dit patroon (na #992/#993) was de trigger om het hier te consolideren, zelfde overweging als
    /// TeamNaamNormalisatie/VeldResolver: één vertaalpunt in plaats van een nieuwe kopie per issue.
    /// </summary>
    private async Task<SportlinkClubResponse<SportlinkMutationResult>> ExecuteMutationWithRetryAsync(
        string functioneleRol,
        Func<string, CancellationToken, Task<SportlinkClubResponse<SportlinkMutationResult>>> putAction,
        CancellationToken cancellationToken)
    {
        var tokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken);
        if (tokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<SportlinkMutationResult>(tokenResult.Status, null, tokenResult.FoutmeldingVoorLog, null);

        var token = tokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return new SportlinkClubResponse<SportlinkMutationResult>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na vernieuwing", null);

        var response = await putAction(token, cancellationToken);

        // Zelfde 401-eenmalige-retry als de read-only methodes.
        if (response.Status == SportlinkClubCallStatus.Ok || response.HttpStatusCode != 401)
            return response;

        InvalidateTokenCache(functioneleRol);
        var retryTokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken, forceRefresh: true);
        if (retryTokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<SportlinkMutationResult>(retryTokenResult.Status, null, retryTokenResult.FoutmeldingVoorLog, null);

        var retryToken = retryTokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(retryToken))
            return new SportlinkClubResponse<SportlinkMutationResult>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na hernieuwing", null);

        var retryResponse = await putAction(retryToken, cancellationToken);
        if (retryResponse.HttpStatusCode == 401)
            return new SportlinkClubResponse<SportlinkMutationResult>(
                SportlinkClubCallStatus.HerkoppelingVereist,
                null,
                "Refresh token is ongeldig (401 blijft terugkomen). Rol moet opnieuw gekoppeld worden.",
                401);

        return retryResponse;
    }

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutDressingRoomsAsync(
        string publicMatchId, string? homeDressingRoomId, string? awayDressingRoomId, string? officialDressingRoomId,
        string token, CancellationToken cancellationToken)
    {
        var body = new
        {
            PublicMatchId = publicMatchId,
            HomeDressingRoomId = homeDressingRoomId,
            AwayDressingRoomId = awayDressingRoomId,
            OfficialDressingRoomId = officialDressingRoomId
        };
        return PutMutationAsync(
            UpdateMatchDressingRoomsEndpoint, "competition/match/UpdateMatchDressingRooms", body, token, cancellationToken);
    }

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutFieldAsync(
        string publicMatchId, string? fieldId, string? fieldSize, int? fieldOffset, bool isForceUpdate,
        string token, CancellationToken cancellationToken)
    {
        var body = new
        {
            PublicMatchId = publicMatchId,
            FieldId = fieldId,
            FieldSize = fieldSize,
            FieldOffset = fieldOffset,
            IsForceUpdate = isForceUpdate
        };
        return PutMutationAsync(
            UpdateMatchFieldEndpoint, "competition/match/UpdateMatchField", body, token, cancellationToken);
    }

    /// <summary>
    /// Gedeelde PUT-uitvoering + responsparsing voor alle mutatie-endpoints — derde bijna-identieke
    /// kopie (na #992/#993) was de trigger om ook dit deel te consolideren, zie de doc-comment op
    /// <see cref="ExecuteMutationWithRetryAsync"/>.
    /// </summary>
    private async Task<SportlinkClubResponse<SportlinkMutationResult>> PutMutationAsync(
        string endpoint, string entityName, object body, string token, CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Navajo-Entity", entityName);
            request.Headers.Add("X-Navajo-Instance", "KNVB");
            request.Headers.Add("X-Navajo-Locale", "nl");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<SportlinkMutationResult>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"Unauthorized bij {entityName} endpoint", 401);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            // Onofficiële, niet-gedocumenteerde API: exacte responsvorm bij een validatiefout niet
            // 100% bevestigd (zie onderzoeksrapport §2.4, "[exacte veldnamen onzeker]" voor een
            // vergelijkbaar endpoint) — probeer daarom zowel het happy-path-veld (IsSuccess) als het
            // violation-pad te lezen, ongeacht HTTP-status, in plaats van non-2xx als harde fout te
            // behandelen. Een échte transport-/serverfout (leeg/kapot JSON) valt terug op SportlinkFout.
            SportlinkMutationResultRaw? raw;
            try
            {
                raw = JsonSerializer.Deserialize<SportlinkMutationResultRaw>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "JSON deserialisatie fout voor {Entity} endpoint", entityName);
                return new SportlinkClubResponse<SportlinkMutationResult>(
                    SportlinkClubCallStatus.SportlinkFout, null, "JSON deserialisatie fout", (int)response.StatusCode);
            }

            if (raw == null)
            {
                _logger.LogWarning("{Entity} endpoint gaf {StatusCode} met lege/onherkenbare respons", entityName, response.StatusCode);
                return new SportlinkClubResponse<SportlinkMutationResult>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"{entityName} endpoint gaf {response.StatusCode} zonder herkenbare respons", (int)response.StatusCode);
            }

            var violations = raw.EntityViolation?.Violations?.Select(v => v.Code ?? "onbekend").ToList();
            var isSuccess = raw.IsSuccess ?? (violations == null || violations.Count == 0) && response.IsSuccessStatusCode;
            return new SportlinkClubResponse<SportlinkMutationResult>(
                SportlinkClubCallStatus.Ok, new SportlinkMutationResult(isSuccess, violations), null, (int)response.StatusCode);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "{Entity} endpoint timeout", entityName);
            return new SportlinkClubResponse<SportlinkMutationResult>(SportlinkClubCallStatus.NetwerkFout, null, $"Timeout bij {entityName} endpoint", null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "{Entity} endpoint netwerk fout", entityName);
            return new SportlinkClubResponse<SportlinkMutationResult>(SportlinkClubCallStatus.NetwerkFout, null, $"Netwerk fout bij {entityName} endpoint", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij {Entity} endpoint", entityName);
            return new SportlinkClubResponse<SportlinkMutationResult>(SportlinkClubCallStatus.SportlinkFout, null, $"Onverwachte fout bij {entityName} endpoint", null);
        }
    }

    // Rauwe deserialisatievorm — nooit publiek: de aanroeper krijgt SportlinkMutationResult
    // (opgeschoonde Violations-lijst), niet deze ongedocumenteerde entityViolation-structuur.
    private sealed record SportlinkMutationResultRaw(
        bool? IsSuccess,
        SportlinkEntityViolationRaw? EntityViolation);

    private sealed record SportlinkEntityViolationRaw(List<SportlinkViolationRaw>? Violations);

    private sealed record SportlinkViolationRaw(string? Code);

    private async Task<SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>> FetchMatchProgramOverviewRawAsync(
        DateOnly datum, string token, CancellationToken cancellationToken)
    {
        try
        {
            var datumStr = datum.ToString("yyyy-MM-dd");
            var url = $"{MatchProgramOverviewEndpoint}?DateFrom={datumStr}&DateTo={datumStr}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Navajo-Entity", "competition/match/MatchProgramOverview");
            request.Headers.Add("X-Navajo-Instance", "KNVB");
            request.Headers.Add("X-Navajo-Locale", "nl");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                    SportlinkClubCallStatus.SportlinkFout, null, "Unauthorized bij MatchProgramOverview endpoint", 401);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("MatchProgramOverview endpoint gaf {StatusCode}: {Body}", response.StatusCode, errorBody);
                return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"MatchProgramOverview endpoint gaf {response.StatusCode}", (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            // Respons-vorm niet 100% bevestigd (array direct, of genest onder "Matches") — zie
            // scripts/dev/Invoke-SportlinkMatchProgramLookup.ps1, waar dit al zo behandeld wordt.
            List<SportlinkMatchProgramEntry>? entries;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var items = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement
                    : doc.RootElement.TryGetProperty("Matches", out var matches) ? matches
                    : doc.RootElement.TryGetProperty("matches", out var matchesLower) ? matchesLower
                    : default;

                if (items.ValueKind != JsonValueKind.Array)
                    return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                        SportlinkClubCallStatus.SportlinkFout, null, "MatchProgramOverview-respons had onverwachte vorm", (int)response.StatusCode);

                entries = JsonSerializer.Deserialize<List<SportlinkMatchProgramEntry>>(items.GetRawText(), JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "JSON deserialisatie fout voor MatchProgramOverview endpoint");
                return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                    SportlinkClubCallStatus.SportlinkFout, null, "JSON deserialisatie fout", (int)response.StatusCode);
            }

            return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                SportlinkClubCallStatus.Ok, entries ?? new List<SportlinkMatchProgramEntry>(), null, (int)response.StatusCode);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MatchProgramOverview endpoint timeout");
            return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(SportlinkClubCallStatus.NetwerkFout, null, "Timeout bij MatchProgramOverview endpoint", null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MatchProgramOverview endpoint netwerk fout");
            return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(SportlinkClubCallStatus.NetwerkFout, null, "Netwerk fout bij MatchProgramOverview endpoint", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij MatchProgramOverview endpoint");
            return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(SportlinkClubCallStatus.SportlinkFout, null, "Onverwachte fout bij MatchProgramOverview endpoint", null);
        }
    }

    private async Task<(SportlinkClubCallStatus Status, string? AccessToken, string? FoutmeldingVoorLog)> RefreshTokenIfNeededAsync(
        string functioneleRol,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var now = DateTimeOffset.UtcNow;

        // Check cache — is token nog geldig?
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
        string token,
        string functioneleRol,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{MatchEndpoint}?PublicMatchId={Uri.EscapeDataString(publicMatchId)}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
