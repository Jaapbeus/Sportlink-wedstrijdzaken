using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// HTTP client voor Sportlink Club API. Handelt token-vernieuwing per functionele rol af, met
/// in-memory caching en per-rol locking. Sinds #992 ook schrijvende aanroepen (nog altijd puur
/// transport — guardrails/audit horen bij de aanroeper, zie <see cref="SportlinkMutationGuard"/>).
/// </summary>
public partial class SportlinkClubClient : ISportlinkClubClient
{
    /// <summary>Keycloak-tokenendpoint van Sportlink — publiek zodat de tokenregistratie
    /// (<c>SportlinkExtensieRollenFunction</c>) dezelfde constante gebruikt in plaats van een kopie (#1122).</summary>
    public const string TokenEndpoint = "https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token";
    private const string MatchEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/Match";
    private const string MatchProgramOverviewEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/MatchProgramOverview";
    private const string UpdateMatchDressingRoomsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/UpdateMatchDressingRooms";
    // Live vastgesteld (2026-09-06, netwerktrace door de eigenaar, #1047): Sportlinks eigen UI
    // roept voor een veldwijziging niet "UpdateMatchField" aan (dat endpoint bestaat niet — gaf
    // HTTP 602 "no valid entity key found") maar "UpdateMatchDetails", met het VOLLEDIGE
    // wedstrijdrecord als payload. Zie UpdateFieldAsync/PutMatchDetailsAsync hieronder.
    private const string UpdateMatchDetailsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/UpdateMatchDetails";
    private const string MatchChangeRequestsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/changerequest/MatchChangeRequests";
    private const string MatchChangeRequestActionEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/changerequest/MatchChangeRequestAction";
    private const string UpdateMatchOfficialsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/official/MatchOfficialsAction";
    private const string UserInfoEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/user/UserInfo";
    // #997: aanmaken van een nieuwe oefenwedstrijd ("clubwedstrijd") — zie CreateClubMatchAsync.
    private const string ClubMatchEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/clubmatch/ClubMatch";
    // #1440: verwijderen van een clubwedstrijd — zie DeleteClubMatchAsync.
    private const string ClubMatchDeleteEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/clubmatch/ClubMatchDelete";
    // #997: de twee ondersteunende picklist-GETs die in deze ronde bewust WEL zijn aangesloten
    // (bewust beperkte scope, zie PR-beschrijving) — read-only, persoonsgegevensvrij.
    private const string PickListsTeamsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/clubmatch/PickListsTeams";
    private const string PickListsLocationEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/clubmatch/PickListsLocation";
    // #1427: de twee overige lijsten van Sportlinks eigen aanmaakformulier (live vastgesteld 01-10-2026).
    private const string ClubMatchDefaultsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/clubmatch/ClubMatchDefaults";
    private const string PickListsMatchInformationEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/clubmatch/PickListsMatchInformation";
    public const string ClientId = "sportlink-club-web";
    private const int TokenExpiryMarginSeconds = 60;

    // #1387: per-aanroep timeouts, losgekoppeld van HttpClient.Timeout (die staat op de .NET-
    // default van 100s als buitenste veiligheidsnet — zie Program.cs van beide tiers, die géén
    // eigen Timeout meer zetten). Zo krijgt élk endpoint een bewust, gemotiveerd budget in plaats
    // van dat één globale waarde voor alles geldt.
    private static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// MatchProgramOverview is het enige Sportlink-endpoint dat hier gedocumenteerd 12+ seconden
    /// duurt (zie SportlinkPublicMatchIdWarmupTimerFunction, docs/SPORTLINK-WEB-EXTENSION.md).
    /// Tegen <see cref="DefaultCallTimeout"/> was de marge nagenoeg nul — elke extra vertraging
    /// timede uit en gaf vóór #1387 een valse HTTP 502 (SportlinkEndpointCore.VertaalStatusNaarFout
    /// kon een timeout niet onderscheiden van een echte Sportlink-fout). Ruimere, uitsluitend voor
    /// dit endpoint gemotiveerde marge — geen wijziging van het budget van de overige, snellere
    /// endpoints.
    /// </summary>
    private static readonly TimeSpan ReverseLookupCallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Wachttijd vóór de ene, begrensde retry bij een transiënte fout (zie
    /// <see cref="IsTransientFout"/>) — los van, en aanvullend op, de bestaande 401-retry in
    /// <see cref="ExecuteWithTokenRetryAsync{T}"/>.</summary>
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(2);

    // #994: dit endpoint/deze body-vorm is NOOIT live bevestigd (reverse-engineered, geen
    // netwerktrace) — daarom staat de mutatie hard op forceDryRun totdat een mens (nooit een
    // agent, zie docs/SPORTLINK-WEB-EXTENSION.md §4.4) een live trace heeft gedaan en deze
    // constante in een aparte, reviewbare PR op true zet. Grep-baar bij naam.
    // Eigenaar: op true gezet op 27-09-2026 na live-bevestiging buiten agent-sessie om (#1319).
    private const bool MatchOfficialsActionLiveBevestigd = true;

    // #995: idem, maar voor het datum/tijd/accommodatie-wijzigingsverzoek — hier bovendien de enige
    // mutatie die een ECHTE tegenstander raakt (Sportlink stuurt bij bevestiging een goedkeurings-
    // verzoek naar de tegenstander). Deze app bouwt uitsluitend stap 1 (valideren, zie
    // RequestMatchChangeAsync) — géén stap 2 (bevestigen). Zelfs stap 1 kan in werkelijkheid al het
    // gevaarlijke moment zijn als Sportlinks eerste PUT geen "dry validate" blijkt te zijn maar
    // direct het verzoek verstuurt — deze code-lock is daarom hier extra belangrijk, niet optioneel.
    // NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
    // beide PUT's en de bevestigingsvlag vastleggen).
    // Eigenaar: op true gezet op 27-09-2026 na live-bevestiging buiten agent-sessie om (#1319).
    private const bool UpdateMatchDetailsChangeRequestLiveBevestigd = true;

    // #997: idem voor het aanmaken van een oefenwedstrijd — dit issue heeft van alle #986-sub-
    // issues de MEESTE onbekenden (volledige body onbevestigd, meerdere picklist-vormen onbekend,
    // delete-methode onbekend). Grep-baar bij naam, zelfde patroon als MatchOfficialsActionLiveBevestigd.
    // Eigenaar: op true gezet op 27-09-2026 na live-bevestiging buiten agent-sessie om (#1319).
    private const bool ClubMatchLiveBevestigd = true;

    // #1440/#1458: verwijderen van een clubwedstrijd. Methode (DELETE), parameter (PublicMatchId als
    // querystring, geen body) en foutvorm (HTTP 420 met Violations) kwamen uit Sportlinks publieke
    // frontend-bundle; de succesrespons is live vastgesteld (03-10-2026): een JSON-object
    // { "PublicMatchId": ..., "IsSuccess": true }, niet leeg. Door de eigenaar op true gezet
    // (besluit 03-10-2026, #1458): de aanroep volgt vanaf nu de club-instelling sportlinkDryRun.
    // Grep-baar bij naam; SportlinkClubMatchDeleteTests bewaakt hem.
    private const bool ClubMatchDeleteLiveBevestigd = true;

    private readonly HttpClient _httpClient;
    private readonly ISportlinkClubTokenStore _tokenStore;
    private readonly SportlinkAutoLoginCoordinator? _autoLogin;
    private readonly ILogger<SportlinkClubClient> _logger;
    private readonly Func<bool> _isDryRun;

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

    /// <param name="httpClient">Onderliggende HTTP-client.</param>
    /// <param name="tokenStore">Rol-gescoped refresh-tokenopslag.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="isDryRun">
    /// #998: delegate (geen settings/DB-afhankelijkheid in <c>Planner.Shared</c>, zelfde
    /// ontkoppelingspatroon als <see cref="ISportlinkClubTokenStore"/>) die bij elke mutatie-
    /// aanroep opnieuw wordt gelezen — niet één keer bij opstarten. <c>true</c> betekent: token-
    /// refresh, snapshot-GETs en de UserInfo-GET lopen echt, maar de daadwerkelijke PUT/POST naar
    /// Sportlink wordt overgeslagen (zie <see cref="PutMutationAsync"/>). Standaard <c>() =&gt;
    /// false</c> als niet meegegeven — backwards compatible met bestaande tests/callers.
    /// </param>
    public SportlinkClubClient(
        HttpClient httpClient,
        ISportlinkClubTokenStore tokenStore,
        ILogger<SportlinkClubClient> logger,
        Func<bool>? isDryRun = null,
        SportlinkAutoLoginCoordinator? autoLogin = null)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _autoLogin = autoLogin;
        _logger = logger;
        _isDryRun = isDryRun ?? (() => false);
    }

    public async Task<SportlinkClubResponse<SportlinkMatch>> GetMatchAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchMatchAsync(publicMatchId, token, functioneleRol, ct), cancellationToken);
    }

    /// <summary>Zelfde token-refresh/401-eenmalige-retry-patroon als <see cref="GetMatchAsync"/>,
    /// maar zonder deserialisatie — zie <see cref="ISportlinkClubClient.GetMatchRawJsonAsync"/>.</summary>
    public async Task<SportlinkClubResponse<string>> GetMatchRawJsonAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchMatchRawJsonAsync(publicMatchId, token, ct), cancellationToken);
    }

    /// <summary>
    /// De ene GET tegen <c>competition/match/Match</c> — <see cref="FetchMatchRawJsonAsync"/>,
    /// <see cref="FetchMatchDetailsSnapshotAsync"/> en <see cref="FetchMatchAsync"/> lazen elk
    /// dezelfde url/headers/verstuur-opbouw; hier gecentraliseerd (#1387) zodat een toekomstige
    /// wijziging aan dat endpoint niet drie keer moet worden doorgevoerd.
    /// </summary>
    private Task<HttpResponseMessage> GetMatchEndpointResponseAsync(
        string publicMatchId, string token, CancellationToken cancellationToken)
    {
        var url = $"{MatchEndpoint}?PublicMatchId={Uri.EscapeDataString(publicMatchId)}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        ZetSportlinkHeaders(request, "competition/match/Match", token);
        return VerstuurMetTimeoutAsync(ct => _httpClient.SendAsync(request, ct), cancellationToken);
    }

    private async Task<SportlinkClubResponse<string>> FetchMatchRawJsonAsync(
        string publicMatchId, string token, CancellationToken cancellationToken)
    {
        try
        {
            var response = await GetMatchEndpointResponseAsync(publicMatchId, token, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<string>(
                    SportlinkClubCallStatus.SportlinkFout, null, "Unauthorized bij match endpoint", 401);

            if (!response.IsSuccessStatusCode)
                return new SportlinkClubResponse<string>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"Match endpoint gaf {response.StatusCode}", (int)response.StatusCode);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return new SportlinkClubResponse<string>(SportlinkClubCallStatus.Ok, json, null, (int)response.StatusCode);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Match endpoint (raw) timeout voor publicMatchId '{PublicMatchId}'", publicMatchId);
            return new SportlinkClubResponse<string>(SportlinkClubCallStatus.NetwerkFout, null, "Timeout bij match endpoint", null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Match endpoint (raw) netwerk fout");
            return new SportlinkClubResponse<string>(SportlinkClubCallStatus.NetwerkFout, null, "Netwerk fout bij match endpoint", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij match endpoint (raw)");
            return new SportlinkClubResponse<string>(SportlinkClubCallStatus.SportlinkFout, null, "Onverwachte fout bij match endpoint", null);
        }
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
        return await ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchMatchProgramOverviewRawAsync(datum, token, ct), cancellationToken);
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

    /// <summary>
    /// Retry-beleid per aanroeptype (#1417). Elke call site kiest expliciet — er is bewust géén
    /// default, zodat een nieuwe mutatie nooit stilzwijgend het leesbeleid erft.
    /// </summary>
    private enum RetryBeleid
    {
        /// <summary>Idempotente GET: bij een transiënte fout (timeout/netwerk/5xx) één keer
        /// opnieuw na <see cref="TransientRetryDelay"/> (#1387).</summary>
        Lezen,

        /// <summary>
        /// Niet-idempotente PUT/POST: <b>géén</b> transiënte retry. Een timeout of gateway-5xx nádat
        /// Sportlink de aanvraag al verwerkt heeft, is voor deze client niet te onderscheiden van
        /// "nooit aangekomen"; herhalen zou dan een tweede oefenwedstrijd aanmaken of een
        /// wijzigingsverzoek tweemaal bij een echte tegenstander afleveren (#1417). De 401-re-auth-
        /// retry blijft wél gelden: die herhaalt pas na een expliciete afwijzing vóór verwerking.
        /// </summary>
        Mutatie
    }

    /// <summary>
    /// Het ene token-refresh/401-eenmalige-retry-pad voor ELKE Sportlink-aanroep, lezend én
    /// schrijvend (#1122; tot dan stond dit patroon vijf keer gekopieerd in de GET-methoden en één
    /// keer generiek voor de PUT's — zelfde overweging als TeamNaamNormalisatie/VeldResolver: één
    /// vertaalpunt in plaats van een kopie per issue). Generiek sinds #995: het teruggegeven type
    /// verschilt per aanroep, de retry-logica niet. Volgorde: token halen/verversen → aanroep →
    /// <i>alleen bij <see cref="RetryBeleid.Lezen"/></i> bij een transiënte fout (timeout/netwerk/5xx,
    /// zie <see cref="IsTransientFout"/>) één keer opnieuw na <see cref="TransientRetryDelay"/>
    /// (#1387, begrensd tot lezen sinds #1417) → bij 401 cache ongeldig maken, geforceerd
    /// verversen, nogmaals opnieuw → blijft het 401, dan is herkoppeling vereist.
    /// </summary>
    private async Task<SportlinkClubResponse<T>> ExecuteWithTokenRetryAsync<T>(
        string functioneleRol,
        RetryBeleid beleid,
        Func<string, CancellationToken, Task<SportlinkClubResponse<T>>> putAction,
        CancellationToken cancellationToken)
        where T : class
    {
        var tokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken);
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
                await WachtVoorTransienteRetryAsync(
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
        InvalidateTokenCache(functioneleRol);
        var retryTokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken, forceRefresh: true);
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

    /// <summary>
    /// Een fout die de moeite waard is om één keer te herhalen (#1387): een timeout/netwerkfout, of
    /// een 5xx van Sportlink zelf — beide zijn typisch van voorbijgaande aard. Een 4xx (los van de
    /// al apart afgehandelde 401) is een inhoudelijke afwijzing en wordt niet beter van herhalen.
    /// Of er daadwerkelijk herhaald wordt, bepaalt <see cref="RetryBeleid"/> (#1417): alleen voor
    /// idempotente leesaanroepen.
    /// </summary>
    private static bool IsTransientFout(SportlinkClubCallStatus status, int? httpStatusCode) =>
        status == SportlinkClubCallStatus.NetwerkFout ||
        (status == SportlinkClubCallStatus.SportlinkFout && httpStatusCode is >= 500 and <= 599);

    /// <summary>Eén gedeelde log+wacht-stap vóór de ene, begrensde transiënte retry (#1387) — zowel
    /// <see cref="ExecuteWithTokenRetryAsync{T}"/> als het token-refreshpad in
    /// <see cref="RefreshTokenIfNeededAsync"/> gebruiken dezelfde stap, alleen de beschrijving in
    /// het logbericht verschilt.</summary>
    private async Task WachtVoorTransienteRetryAsync(string beschrijving, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Transiënte Sportlink-fout ({Beschrijving}) — één retry na {Delay}s",
            beschrijving, TransientRetryDelay.TotalSeconds);
        await Task.Delay(TransientRetryDelay, cancellationToken);
    }

    /// <summary>Kortere vorm voor de meerderheid van de aanroepen, die <see cref="DefaultCallTimeout"/>
    /// gebruiken — alleen de gedocumenteerd trage reverse-lookup geeft expliciet
    /// <see cref="ReverseLookupCallTimeout"/> mee via de overload hieronder.</summary>
    private static Task<HttpResponseMessage> VerstuurMetTimeoutAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> aanroep, CancellationToken cancellationToken)
        => VerstuurMetTimeoutAsync(aanroep, DefaultCallTimeout, cancellationToken);

    /// <summary>
    /// Verstuurt <paramref name="aanroep"/> met een eigen, per-aanroep timeout (#1387) bovenop de
    /// aanroeper-<paramref name="cancellationToken"/> — zie <see cref="DefaultCallTimeout"/>/
    /// <see cref="ReverseLookupCallTimeout"/>. Bij het aflopen van ONZE timeout gooien we bewust
    /// dezelfde <see cref="TaskCanceledException"/> als een aanroeper-annulering zou geven, zodat
    /// elke bestaande <c>catch (TaskCanceledException)</c> per endpoint ongewijzigd blijft werken —
    /// alleen een échte annulering door de aanroeper zelf wordt ongewijzigd doorgegeven.
    /// </summary>
    private static async Task<HttpResponseMessage> VerstuurMetTimeoutAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> aanroep,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await aanroep(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException($"Sportlink-aanroep afgebroken na {timeout.TotalSeconds}s (per-aanroep timeout).");
        }
    }

    /// <summary>De drie Navajo-headers + Bearer-token die élke Sportlink Club-aanroep draagt
    /// (docs/SPORTLINK-WEB-EXTENSION.md §6.2) — één plek in plaats van acht kopieën (#1122).</summary>
    private static void ZetSportlinkHeaders(HttpRequestMessage request, string entityName, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Navajo-Entity", entityName);
        request.Headers.Add("X-Navajo-Instance", "KNVB");
        request.Headers.Add("X-Navajo-Locale", "nl");
    }

}
