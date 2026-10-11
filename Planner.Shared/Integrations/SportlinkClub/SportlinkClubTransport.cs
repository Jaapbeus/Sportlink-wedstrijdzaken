using Microsoft.Extensions.Logging;
using static Planner.Shared.Integrations.SportlinkClub.SportlinkClubHttp;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>De enige route naar Sportlink: token ophalen/verversen, eenmalige 401-herhaling en het retrybeleid (#1631, verplaatst uit SportlinkClubClient). Alle domeinklassen lopen via <see cref="ExecuteWithTokenRetryAsync{T}"/>.</summary>
internal sealed class SportlinkClubTransport
{
    private readonly ILogger _logger;
    private readonly SportlinkClubTokenBeheer _tokens;

    internal HttpClient Http { get; }
    internal ILogger Logger => _logger;
    internal Func<bool> IsDryRun { get; }

    internal SportlinkClubTransport(HttpClient httpClient, ISportlinkClubTokenStore tokenStore, ILogger logger, Func<bool> isDryRun, SportlinkAutoLoginCoordinator? autoLogin)
    {
        Http = httpClient;
        _logger = logger;
        IsDryRun = isDryRun;
        _tokens = new SportlinkClubTokenBeheer(httpClient, tokenStore, autoLogin, logger);
    }

    internal Task<SportlinkClubCallStatus> VerversTokenAsync(string functioneleRol, CancellationToken cancellationToken)
        => _tokens.VerversTokenAsync(functioneleRol, cancellationToken);

    private const string MatchEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/Match";

    /// <summary>
    /// De ene GET tegen <c>competition/match/Match</c> — <c>FetchMatchRawJsonAsync</c>,
    /// <c>FetchMatchDetailsSnapshotAsync</c> en <c>FetchMatchAsync</c> lazen elk
    /// dezelfde url/headers/verstuur-opbouw; hier gecentraliseerd (#1387) zodat een toekomstige
    /// wijziging aan dat endpoint niet drie keer moet worden doorgevoerd.
    /// </summary>
    internal Task<HttpResponseMessage> GetMatchEndpointResponseAsync(
        string publicMatchId, string token, CancellationToken cancellationToken)
    {
        var url = $"{MatchEndpoint}?PublicMatchId={Uri.EscapeDataString(publicMatchId)}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        ZetSportlinkHeaders(request, "competition/match/Match", token);
        return VerstuurMetTimeoutAsync(ct => Http.SendAsync(request, ct), cancellationToken);
    }

    /// <summary>
    /// Het ene token-refresh/401-eenmalige-retry-pad voor ELKE Sportlink-aanroep, lezend én
    /// schrijvend (#1122; tot dan stond dit patroon vijf keer gekopieerd in de GET-methoden en één
    /// keer generiek voor de PUT's — zelfde overweging als TeamNaamNormalisatie/VeldResolver: één
    /// vertaalpunt in plaats van een kopie per issue). Generiek sinds #995: het teruggegeven type
    /// verschilt per aanroep, de retry-logica niet. Volgorde: token halen/verversen → aanroep →
    /// <i>alleen bij <see cref="RetryBeleid.Lezen"/></i> bij een transiënte fout (timeout/netwerk/5xx,
    /// zie <see cref="SportlinkClubHttp.IsTransientFout"/>) één keer opnieuw na <see cref="SportlinkClubHttp.TransientRetryDelay"/>
    /// (#1387, begrensd tot lezen sinds #1417) → bij 401 cache ongeldig maken, geforceerd
    /// verversen, nogmaals opnieuw → blijft het 401, dan is herkoppeling vereist.
    /// </summary>
    internal async Task<SportlinkClubResponse<T>> ExecuteWithTokenRetryAsync<T>(
        string functioneleRol,
        RetryBeleid beleid,
        Func<string, CancellationToken, Task<SportlinkClubResponse<T>>> putAction,
        CancellationToken cancellationToken)
        where T : class
    {
        var tokenResult = await _tokens.RefreshTokenIfNeededAsync(functioneleRol, cancellationToken);
        if (tokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<T>(tokenResult.Status, null, tokenResult.FoutmeldingVoorLog, null);

        var token = tokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return new SportlinkClubResponse<T>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na vernieuwing", null);

        var response = await putAction(token, cancellationToken);

        if (IsTransientFout(response.Status, response.HttpStatusCode))
        {
            if (beleid == RetryBeleid.Lezen)
            {
                await WachtVoorTransienteRetryAsync(_logger, 
                    $"{response.Status}, HTTP {response.HttpStatusCode}, rol '{functioneleRol}'", cancellationToken);
                response = await putAction(token, cancellationToken);
            }
            else
            {
                // #1417: een mutatie wordt NOOIT automatisch herhaald — de uitkomst van de eerste
                // poging is onbekend (mogelijk al verwerkt). De beheerder krijgt via
                // SportlinkEndpointCore.VertaalStatusNaarFout(isMutatie: true) de instructie om
                // eerst in Sportlink te controleren. Nooit de body loggen (CISO-regel).
                _logger.LogWarning(
                    "Transiënte fout ({Status}, HTTP {HttpStatus}) op een Sportlink-mutatie voor rol '{Rol}' — bewust niet herhaald (#1417); uitkomst bij Sportlink onbekend.",
                    response.Status, response.HttpStatusCode, functioneleRol);
            }
        }

        if (response.Status == SportlinkClubCallStatus.Ok || response.HttpStatusCode != 401)
            return response;

        _logger.LogInformation("401 ontvangen voor rol '{Rol}', cache wordt ongeldig gemaakt en opnieuw geprobeerd", functioneleRol);
        _tokens.InvalidateTokenCache(functioneleRol);
        var retryTokenResult = await _tokens.RefreshTokenIfNeededAsync(functioneleRol, cancellationToken, forceRefresh: true);
        if (retryTokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<T>(retryTokenResult.Status, null, retryTokenResult.FoutmeldingVoorLog, null);

        var retryToken = retryTokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(retryToken))
            return new SportlinkClubResponse<T>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na hernieuwing", null);

        var retryResponse = await putAction(retryToken, cancellationToken);
        if (retryResponse.HttpStatusCode == 401)
            return new SportlinkClubResponse<T>(
                SportlinkClubCallStatus.HerkoppelingVereist,
                null,
                "Refresh token is ongeldig (401 blijft terugkomen). Rol moet opnieuw gekoppeld worden.",
                401);

        return retryResponse;
    }
}
