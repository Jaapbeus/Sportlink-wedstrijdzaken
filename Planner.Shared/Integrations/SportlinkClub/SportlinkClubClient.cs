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
public class SportlinkClubClient : ISportlinkClubClient
{
    private const string TokenEndpoint = "https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token";
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
    private const string ClientId = "sportlink-club-web";
    private const int TokenExpiryMarginSeconds = 60;

    // #994: dit endpoint/deze body-vorm is NOOIT live bevestigd (reverse-engineered, geen
    // netwerktrace) — daarom staat de mutatie hard op forceDryRun totdat een mens (nooit een
    // agent, zie docs/SPORTLINK-WEB-EXTENSION.md §4.4) een live trace heeft gedaan en deze
    // constante in een aparte, reviewbare PR op true zet. Grep-baar bij naam.
    private const bool MatchOfficialsActionLiveBevestigd = false;

    // #995: idem, maar voor het datum/tijd/accommodatie-wijzigingsverzoek — hier bovendien de enige
    // mutatie die een ECHTE tegenstander raakt (Sportlink stuurt bij bevestiging een goedkeurings-
    // verzoek naar de tegenstander). Deze app bouwt uitsluitend stap 1 (valideren, zie
    // RequestMatchChangeAsync) — géén stap 2 (bevestigen). Zelfs stap 1 kan in werkelijkheid al het
    // gevaarlijke moment zijn als Sportlinks eerste PUT geen "dry validate" blijkt te zijn maar
    // direct het verzoek verstuurt — deze code-lock is daarom hier extra belangrijk, niet optioneel.
    // NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
    // beide PUT's en de bevestigingsvlag vastleggen).
    private const bool UpdateMatchDetailsChangeRequestLiveBevestigd = false;

    private readonly HttpClient _httpClient;
    private readonly ISportlinkClubTokenStore _tokenStore;
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
        Func<bool>? isDryRun = null)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _logger = logger;
        _isDryRun = isDryRun ?? (() => false);
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

    /// <summary>Zelfde token-refresh/401-eenmalige-retry-patroon als <see cref="GetMatchAsync"/>,
    /// maar zonder deserialisatie — zie <see cref="ISportlinkClubClient.GetMatchRawJsonAsync"/>.</summary>
    public async Task<SportlinkClubResponse<string>> GetMatchRawJsonAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken);
        if (tokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<string>(tokenResult.Status, null, tokenResult.FoutmeldingVoorLog, null);

        var token = tokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return new SportlinkClubResponse<string>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na vernieuwing", null);

        var response = await FetchMatchRawJsonAsync(publicMatchId, token, cancellationToken);

        if (response.Status == SportlinkClubCallStatus.Ok || response.HttpStatusCode != 401)
            return response;

        InvalidateTokenCache(functioneleRol);
        var retryTokenResult = await RefreshTokenIfNeededAsync(functioneleRol, cancellationToken, forceRefresh: true);
        if (retryTokenResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<string>(retryTokenResult.Status, null, retryTokenResult.FoutmeldingVoorLog, null);

        var retryToken = retryTokenResult.AccessToken;
        if (string.IsNullOrWhiteSpace(retryToken))
            return new SportlinkClubResponse<string>(
                SportlinkClubCallStatus.SportlinkFout, null, "Access token is leeg na hernieuwing", null);

        var retryResponse = await FetchMatchRawJsonAsync(publicMatchId, retryToken, cancellationToken);
        if (retryResponse.HttpStatusCode == 401)
            return new SportlinkClubResponse<string>(
                SportlinkClubCallStatus.HerkoppelingVereist,
                null,
                "Refresh token is ongeldig (401 blijft terugkomen). Rol moet opnieuw gekoppeld worden.",
                401);

        return retryResponse;
    }

    private async Task<SportlinkClubResponse<string>> FetchMatchRawJsonAsync(
        string publicMatchId, string token, CancellationToken cancellationToken)
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
            async (token, ct) =>
            {
                // Live vastgesteld (2026-09-06, #1047): UpdateMatchDetails verwacht het VOLLEDIGE
                // wedstrijdrecord, niet een klein veld-only patch — Sportlinks eigen UI stuurt
                // gewoon het al opgehaalde match-object terug met het gewijzigde veld erin. Daarom
                // hier eerst een verse snapshot ophalen (dezelfde Match-GET, rijker gemodelleerd)
                // in plaats van de aanroeper de volledige payload te laten opgeven.
                var snapshot = await FetchMatchDetailsSnapshotAsync(publicMatchId, token, ct);
                if (snapshot.Status != SportlinkClubCallStatus.Ok || snapshot.Data == null)
                    return new SportlinkClubResponse<SportlinkMutationResult>(
                        snapshot.Status == SportlinkClubCallStatus.Ok ? SportlinkClubCallStatus.SportlinkFout : snapshot.Status,
                        null, snapshot.FoutmeldingVoorLog ?? "Kon wedstrijdgegevens niet ophalen voor veldwijziging", snapshot.HttpStatusCode);

                // Live vastgesteld (2026-09-06, #1048): een lege PublicApplicantId wordt door
                // Sportlink geaccepteerd voor een eigen-veld-wijziging (bevestigd: isSuccess:true,
                // audit-log Success). Dat veld is kennelijk alleen relevant voor #996's change-
                // request-actie (waar een externe partij "aanvraagt"), niet voor een directe
                // wijziging door de club zelf aan haar eigen wedstrijd — dus geen aparte
                // UserInfo-aanroep (en de daar nog openstaande bug, #1048) nodig voor dit pad.
                return await PutMatchDetailsAsync(
                    publicMatchId, "", snapshot.Data,
                    fieldId, fieldSize, fieldOffset, isForceUpdate, token, ct);
            },
            cancellationToken);

    // NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
    // beide PUT's en de bevestigingsvlag vastleggen). Dit is uitsluitend stap 1 (valideren) — er
    // bestaat bewust geen stap 2 (bevestigen) in deze client, geen endpoint en geen UI-knop ervoor.
    /// <summary>
    /// Vraagt een wijziging van datum/tijd/accommodatie aan (#995, epic #986) — stap 1 (valideren)
    /// van Sportlinks tweestaps flow, via hetzelfde endpoint als #993's veld-wijziging:
    /// <c>PUT competition/match/UpdateMatchDetails</c>. <b>ONBEVESTIGD, altijd code-gelockt</b>
    /// (zie <see cref="UpdateMatchDetailsChangeRequestLiveBevestigd"/>) — dit is de enige
    /// Sportlink-mutatie die een ECHTE tegenstander raakt, dus ONAFHANKELIJK van de club-instelling
    /// <c>sportlinkDryRun</c> loopt elke aanroep hier via de forceDryRun-lock totdat een mens (nooit
    /// een agent, zie docs/SPORTLINK-WEB-EXTENSION.md §4.4) een live trace heeft gedaan.
    /// <para>
    /// Bewust GEEN gedeelde refactor van <see cref="PutMatchDetailsAsync"/>: die methode hoort bij
    /// #993's live-bevestigde, werkende productiepad. Deze methode kopieert de structuur (verse
    /// snapshot ophalen → envelope met alléén het gewijzigde veld overschreven) in plaats van de
    /// bestaande methode te parametriseren — code-duplicatie is hier de veiligere keuze.
    /// </para>
    /// <b>De aanroeper controleert VOORAF</b> <c>SportlinkMutationGuard.MagMuteren(match,
    /// SportlinkMutationSoort.DatumTijdAccommodatie)</c>.
    /// </summary>
    /// <param name="functioneleRol">Functionele rol voor token-lookup.</param>
    /// <param name="publicMatchId">Zie de TODO(#987)-waarschuwing op <see cref="GetMatchAsync"/>.</param>
    /// <param name="nieuweDatum">Nieuwe wedstrijddatum, of <c>null</c> om de datum ongewijzigd te laten.</param>
    /// <param name="nieuweStartTijd">Nieuwe starttijd, of <c>null</c> om de tijd ongewijzigd te laten.</param>
    /// <param name="nieuweFacilityId">Nieuwe accommodatie-ID, of <c>null</c> om de accommodatie ongewijzigd te laten.</param>
    /// <param name="toelichting">Verplichte toelichting bij het verzoek — validatie hiervan is aan de aanroeper.</param>
    /// <returns>
    /// Bij <c>Status=Ok</c>: <c>Data.Mutatie.IsForcedDryRun</c> is in de praktijk altijd <c>true</c>
    /// zolang de code-lock actief is, en <c>Data.Validatie</c> dus altijd <c>null</c> (er is dan
    /// nooit een echte Sportlink-respons om te parsen).
    /// </returns>
    public Task<SportlinkClubResponse<SportlinkMatchChangeRequestResult>> RequestMatchChangeAsync(
        string functioneleRol,
        string publicMatchId,
        DateOnly? nieuweDatum,
        TimeOnly? nieuweStartTijd,
        string? nieuweFacilityId,
        string toelichting,
        CancellationToken cancellationToken = default)
        => ExecuteMutationWithRetryAsync<SportlinkMatchChangeRequestResult>(
            functioneleRol,
            async (token, ct) =>
            {
                // Zelfde reden als UpdateFieldAsync hierboven: UpdateMatchDetails verwacht het
                // volledige wedstrijdrecord, dus eerst een verse snapshot ophalen.
                var snapshot = await FetchMatchDetailsSnapshotAsync(publicMatchId, token, ct);
                if (snapshot.Status != SportlinkClubCallStatus.Ok || snapshot.Data == null)
                    return new SportlinkClubResponse<SportlinkMatchChangeRequestResult>(
                        snapshot.Status == SportlinkClubCallStatus.Ok ? SportlinkClubCallStatus.SportlinkFout : snapshot.Status,
                        null, snapshot.FoutmeldingVoorLog ?? "Kon wedstrijdgegevens niet ophalen voor wijzigingsverzoek", snapshot.HttpStatusCode);

                return await PutMatchDetailsChangeRequestAsync(
                    publicMatchId, snapshot.Data, nieuweDatum, nieuweStartTijd, nieuweFacilityId, toelichting, token, ct);
            },
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
                var element = UnwrapArrayEnvelope(doc.RootElement, "ChangeRequests", "changeRequests", "Items");

                if (element is not { ValueKind: JsonValueKind.Array } arrayElement)
                    return new SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>(
                        SportlinkClubCallStatus.SportlinkFout, null, "MatchChangeRequests-respons had onverwachte vorm", (int)response.StatusCode);

                items = JsonSerializer.Deserialize<List<SportlinkChangeRequest>>(arrayElement.GetRawText(), JsonOptions);
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

    /// <summary>
    /// Wijst officials (scheidsrechter/assistenten) toe aan een wedstrijd (#994, epic #986) —
    /// <c>PUT competition/match/official/MatchOfficialsAction</c>. <b>ONBEVESTIGD</b>: endpoint en
    /// body-vorm komen uit Sportlinks eigen frontend-code, nooit met een netwerktrace gezien — deze
    /// aanroep loopt daarom altijd via de code-lock (<see cref="MatchOfficialsActionLiveBevestigd"/>
    /// <c>= false</c>), ONAFHANKELIJK van de club-instelling <c>sportlinkDryRun</c>. Zie
    /// <see cref="SportlinkOfficialToewijzing"/> voor de aannames op elementniveau.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkMutationResult>> AssignOfficialsAsync(
        string functioneleRol,
        string publicMatchId,
        IReadOnlyList<SportlinkOfficialToewijzing> officials,
        CancellationToken cancellationToken = default)
        => ExecuteMutationWithRetryAsync(
            functioneleRol,
            (token, ct) => PutMatchOfficialsAsync(publicMatchId, officials, token, ct),
            cancellationToken);

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutMatchOfficialsAsync(
        string publicMatchId, IReadOnlyList<SportlinkOfficialToewijzing> officials, string token, CancellationToken cancellationToken)
    {
        return PutMutationAsync(
            UpdateMatchOfficialsEndpoint,
            "competition/match/official/MatchOfficialsAction",
            BuildMatchOfficialsBody(publicMatchId, officials),
            token,
            cancellationToken,
            forceDryRun: !MatchOfficialsActionLiveBevestigd,
            verrijkResultaat: VerrijkOfficialsResultaat);
    }

    /// <summary>
    /// Bouwt de <c>MatchOfficialsAction</c>-requestbody — losgetrokken van <see cref="PutMatchOfficialsAsync"/>
    /// zodat de AANGENOMEN, NOG NIET LIVE BEVESTIGDE vorm (#994: "OfficialPosition"/"PersoonId" als
    /// veldnamen binnen elk element van <c>OfficialsToBeAssigned</c> — zie
    /// <see cref="SportlinkOfficialToewijzing"/>) direct getest kan worden, ook al gaat er door de
    /// forceDryRun-lock nooit een echte PUT met deze body uit.
    /// </summary>
    internal static object BuildMatchOfficialsBody(string publicMatchId, IReadOnlyList<SportlinkOfficialToewijzing> officials) =>
        new
        {
            PublicMatchId = publicMatchId,
            OfficialsToBeAssigned = officials
                .Select(o => new { OfficialPosition = o.OfficialPosition, PersoonId = o.PersoonId })
                .ToList()
        };

    /// <summary>
    /// Taakspecifieke uitbreiding op de generieke responsparser (#994): Sportlink toont "opgeslagen
    /// met fouten" als één official een <c>ValidationDescription</c> heeft — ook al is de HTTP-status
    /// 200 en <c>Error</c> niet gezet. De generieke <see cref="PutMutationAsync"/>-parsing (gericht op
    /// het `{Error, ViolationCodes, Violations}`-afwijzingspatroon) ziet dit niet, dus wordt het
    /// resultaat hier ná die generieke parsing alsnog gecorrigeerd. Nooit persoonsgegevens loggen —
    /// deze methode logt niets, geeft alleen de omschrijvingstekst door als violation.
    /// </summary>
    internal static SportlinkMutationResult VerrijkOfficialsResultaat(string json, SportlinkMutationResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Officials", out var officialsElement) ||
                officialsElement.ValueKind != JsonValueKind.Array)
                return result;

            var validatieMeldingen = new List<string>();
            foreach (var official in officialsElement.EnumerateArray())
            {
                if (official.ValueKind == JsonValueKind.Object &&
                    official.TryGetProperty("ValidationDescription", out var validationElement) &&
                    validationElement.ValueKind == JsonValueKind.String)
                {
                    var melding = validationElement.GetString();
                    if (!string.IsNullOrWhiteSpace(melding))
                        validatieMeldingen.Add(melding);
                }
            }

            if (validatieMeldingen.Count == 0)
                return result;

            // Sportlink toont "opgeslagen met fouten" (IS_SAVED_WITH_ERRORS) — dat is inhoudelijk
            // geen succes, ook al was de HTTP-status 200/Error niet gezet.
            return result with { IsSuccess = false, Violations = validatieMeldingen };
        }
        catch (JsonException)
        {
            // Onherkenbare respons-vorm: geen extra fout hierboven op stapelen, laat het generieke
            // resultaat (op basis van HTTP-status/Error) ongewijzigd.
            return result;
        }
    }

    private sealed record SportlinkUserInfo(string? PublicPersonId);

    /// <summary>
    /// Gedeelde token-refresh/401-eenmalige-retry-wrapper voor alle schrijvende Sportlink-aanroepen
    /// (#992 kleedkamers, #993 veld, en toekomstige mutaties) — derde bijna-identieke kopie van
    /// dit patroon (na #992/#993) was de trigger om het hier te consolideren, zelfde overweging als
    /// TeamNaamNormalisatie/VeldResolver: één vertaalpunt in plaats van een nieuwe kopie per issue.
    /// Generiek gemaakt bij #995 zodat <see cref="RequestMatchChangeAsync"/> hetzelfde
    /// token-refresh/retry-gedrag krijgt zonder de wrapper zelf te dupliceren — het teruggegeven
    /// type verschilt per mutatie (<see cref="SportlinkMutationResult"/> voor de bestaande mutaties,
    /// <see cref="SportlinkMatchChangeRequestResult"/> voor #995), de retry-logica niet.
    /// </summary>
    private async Task<SportlinkClubResponse<T>> ExecuteMutationWithRetryAsync<T>(
        string functioneleRol,
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

        // Zelfde 401-eenmalige-retry als de read-only methodes.
        if (response.Status == SportlinkClubCallStatus.Ok || response.HttpStatusCode != 401)
            return response;

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

    /// <summary>
    /// Bouwt de volledige <c>UpdateMatchDetails</c>-envelope door <paramref name="snapshot"/> (het
    /// net opgehaalde, huidige wedstrijdrecord) terug te sturen met alleen het veld/velddeel
    /// overschreven — exact het patroon dat Sportlinks eigen UI gebruikt (live vastgesteld,
    /// #1047). Elk ander veld in <c>MatchData</c> komt dus altijd van de server zelf, nooit van
    /// een aanname hier — voorkomt dat een verouderd of onvolledig lokaal model een echt
    /// wedstrijdveld (teamnaam, uitslag, ...) zou overschrijven.
    /// </summary>
    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutMatchDetailsAsync(
        string publicMatchId, string publicApplicantId, SportlinkMatchDetailsSnapshot snapshot,
        string? fieldId, string? fieldSize, int? fieldOffset, bool isForceUpdate,
        string token, CancellationToken cancellationToken)
    {
        var matchData = new MatchDataBody(
            AgeClassCode: snapshot.AgeClassCode,
            AssemblyTime: snapshot.MatchDetails?.MatchDetailsHome?.AssemblyTime?.Value,
            AwayScore: snapshot.Result?.AwayScore,
            AwayTeam: snapshot.Teams?.Away?.TeamName,
            DepartureTime: null,
            Description: snapshot.MatchDetails?.Description?.Value,
            Drivers: "",
            Duration: snapshot.Duration,
            ExternalMatchId: snapshot.ExternalMatchId,
            FacilityId: snapshot.MatchField?.FacilityId,
            FieldId: fieldId ?? snapshot.Field?.FieldId,
            FieldOffset: fieldOffset ?? snapshot.Field?.FieldOffset,
            FieldSize: fieldSize ?? snapshot.Field?.FieldSize,
            HomeScore: snapshot.Result?.HomeScore,
            HomeTeam: snapshot.Teams?.Home?.TeamName,
            MatchChangeRequestRemarks: "",
            MatchDate: snapshot.MatchDate?.Date,
            MatchStatus: snapshot.MatchStatus,
            PublicAwayTeamId: snapshot.Teams?.Away?.PublicTeamId,
            PublicHomeTeamId: snapshot.Teams?.Home?.PublicTeamId,
            SportIdTag: snapshot.Sport?.IdTag,
            StartTime: snapshot.MatchDate?.StartTime);

        var body = new UpdateMatchDetailsBody(
            ConfirmationNeeded: null,
            IsForceUpdate: isForceUpdate,
            IsMatchChangeRequestMandatory: false,
            IsOwnFacility: true,
            IsPlannableByClub: false,
            IsSuccess: false,
            PublicApplicantId: publicApplicantId,
            PublicMatchId: publicMatchId,
            MatchData: matchData);

        return PutMutationAsync(
            UpdateMatchDetailsEndpoint, "competition/match/UpdateMatchDetails", body, token, cancellationToken);
    }

    // NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
    // beide PUT's en de bevestigingsvlag vastleggen).
    /// <summary>
    /// Bouwt en verstuurt de <c>UpdateMatchDetails</c>-envelope voor een datum/tijd/accommodatie-
    /// wijzigingsverzoek (#995) — bewust GEEN parametrisering van <see cref="PutMatchDetailsAsync"/>
    /// (#993's live-bevestigde veld-wijzigingspad), zie de doc-comment op
    /// <see cref="RequestMatchChangeAsync"/>. Deze aanroep is altijd forceDryRun-gelockt (zie
    /// <see cref="UpdateMatchDetailsChangeRequestLiveBevestigd"/>): er gaat dus nooit een echte PUT
    /// uit, en <see cref="ParseMatchChangeValidatie"/> wordt bijgevolg ook nooit in de praktijk
    /// aangeroepen zolang de lock actief is (de <c>verrijkResultaat</c>-hook loopt pas ná een echte
    /// HTTP-respons, die er in dry-run-modus nooit komt).
    /// </summary>
    private async Task<SportlinkClubResponse<SportlinkMatchChangeRequestResult>> PutMatchDetailsChangeRequestAsync(
        string publicMatchId, SportlinkMatchDetailsSnapshot snapshot,
        DateOnly? nieuweDatum, TimeOnly? nieuweStartTijd, string? nieuweFacilityId, string toelichting,
        string token, CancellationToken cancellationToken)
    {
        var body = BuildMatchChangeRequestBody(publicMatchId, snapshot, nieuweDatum, nieuweStartTijd, nieuweFacilityId, toelichting);

        SportlinkMatchChangeValidatie? validatie = null;
        var mutationResponse = await PutMutationAsync(
            UpdateMatchDetailsEndpoint, "competition/match/UpdateMatchDetails", body, token, cancellationToken,
            forceDryRun: !UpdateMatchDetailsChangeRequestLiveBevestigd,
            verrijkResultaat: (json, result) =>
            {
                validatie = ParseMatchChangeValidatie(json);
                return result;
            });

        if (mutationResponse.Status != SportlinkClubCallStatus.Ok || mutationResponse.Data == null)
            return new SportlinkClubResponse<SportlinkMatchChangeRequestResult>(
                mutationResponse.Status, null, mutationResponse.FoutmeldingVoorLog, mutationResponse.HttpStatusCode);

        return new SportlinkClubResponse<SportlinkMatchChangeRequestResult>(
            SportlinkClubCallStatus.Ok,
            new SportlinkMatchChangeRequestResult(mutationResponse.Data, validatie),
            null,
            mutationResponse.HttpStatusCode);
    }

    /// <summary>
    /// Bouwt de <c>UpdateMatchDetails</c>-envelope voor #995 — zelfde patroon als
    /// <see cref="PutMatchDetailsAsync"/> (snapshot terugsturen met alléén het gewijzigde deel
    /// overschreven), maar met <c>MatchDate</c>/<c>StartTime</c>/<c>FacilityId</c>/
    /// <c>MatchChangeRequestRemarks</c> als overschrijfbare velden in plaats van het veld. Hergebruikt
    /// bewust de bestaande <see cref="MatchDataBody"/>/<see cref="UpdateMatchDetailsBody"/>-records
    /// (pure databehouders, geen logica) — alleen de BOUW-methode is een eigen kopie.
    /// <para>
    /// <b>ONBEVESTIGD (#995):</b> <c>IsMatchChangeRequestMandatory: true</c> en een lege
    /// <c>PublicApplicantId</c> zijn aannames — #993's veld-wijziging gebruikt <c>false</c> resp. een
    /// live-bevestigde lege string voor een EIGEN-veld-wijziging, geen wijzigingsverzoek aan een
    /// tegenstander. Beide zijn nooit met een netwerktrace gezien voor dit specifieke pad.
    /// </para>
    /// </summary>
    internal static object BuildMatchChangeRequestBody(
        string publicMatchId, SportlinkMatchDetailsSnapshot snapshot,
        DateOnly? nieuweDatum, TimeOnly? nieuweStartTijd, string? nieuweFacilityId, string toelichting)
    {
        var matchData = new MatchDataBody(
            AgeClassCode: snapshot.AgeClassCode,
            AssemblyTime: snapshot.MatchDetails?.MatchDetailsHome?.AssemblyTime?.Value,
            AwayScore: snapshot.Result?.AwayScore,
            AwayTeam: snapshot.Teams?.Away?.TeamName,
            DepartureTime: null,
            Description: snapshot.MatchDetails?.Description?.Value,
            Drivers: "",
            Duration: snapshot.Duration,
            ExternalMatchId: snapshot.ExternalMatchId,
            FacilityId: nieuweFacilityId ?? snapshot.MatchField?.FacilityId,
            FieldId: snapshot.Field?.FieldId,
            FieldOffset: snapshot.Field?.FieldOffset,
            FieldSize: snapshot.Field?.FieldSize,
            HomeScore: snapshot.Result?.HomeScore,
            HomeTeam: snapshot.Teams?.Home?.TeamName,
            MatchChangeRequestRemarks: toelichting,
            MatchDate: nieuweDatum?.ToString("yyyy-MM-dd") ?? snapshot.MatchDate?.Date,
            MatchStatus: snapshot.MatchStatus,
            PublicAwayTeamId: snapshot.Teams?.Away?.PublicTeamId,
            PublicHomeTeamId: snapshot.Teams?.Home?.PublicTeamId,
            SportIdTag: snapshot.Sport?.IdTag,
            StartTime: nieuweStartTijd?.ToString("HH:mm:ss") ?? snapshot.MatchDate?.StartTime);

        return new UpdateMatchDetailsBody(
            ConfirmationNeeded: null,
            IsForceUpdate: false,
            IsMatchChangeRequestMandatory: true,
            IsOwnFacility: true,
            IsPlannableByClub: false,
            IsSuccess: false,
            PublicApplicantId: "",
            PublicMatchId: publicMatchId,
            MatchData: matchData);
    }

    /// <summary>
    /// Parseert Sportlinks <c>ConfirmationNeeded</c>-veld uit een <c>UpdateMatchDetails</c>-respons
    /// (#995) naar <see cref="SportlinkMatchChangeValidatie"/>. <b>ONBEVESTIGD:</b> de exacte vorm is
    /// nooit met een netwerktrace gezien — <c>ValidationResultMessages</c>-elementen kunnen kale
    /// strings zijn óf objecten met een <c>Message</c>- of <c>Description</c>-veld (issue #995:
    /// "elementvorm onbekend"), en <c>HasBlockingMessages</c> kan zowel genest onder
    /// <c>ConfirmationNeeded</c> als op het toplevel staan — deze parser probeert beide, zelfde
    /// defensieve stijl als <see cref="VerrijkOfficialsResultaat"/>/<see cref="UnwrapArrayEnvelope"/>.
    /// Geeft <c>null</c> terug bij een onherkenbare JSON-vorm — nooit een gok naar de aanroeper.
    /// </summary>
    internal static SportlinkMatchChangeValidatie? ParseMatchChangeValidatie(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("ConfirmationNeeded", out var confirmationElement) ||
                confirmationElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new SportlinkMatchChangeValidatie(false, Array.Empty<string>(), false);

            var meldingen = new List<string>();
            if (confirmationElement.ValueKind == JsonValueKind.Object &&
                confirmationElement.TryGetProperty("ValidationResultMessages", out var messagesElement) &&
                messagesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in messagesElement.EnumerateArray())
                {
                    string? tekst = item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Object when item.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString(),
                        JsonValueKind.Object when item.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String => d.GetString(),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(tekst))
                        meldingen.Add(tekst);
                }
            }

            // Positie van HasBlockingMessages niet bevestigd — genest onder ConfirmationNeeded en
            // toplevel allebei proberen, genest weegt zwaarder (issue-tekst noemt ze als één geheel).
            var hasBlocking =
                (confirmationElement.ValueKind == JsonValueKind.Object &&
                 confirmationElement.TryGetProperty("HasBlockingMessages", out var nestedBlocking) &&
                 nestedBlocking.ValueKind == JsonValueKind.True)
                || (doc.RootElement.TryGetProperty("HasBlockingMessages", out var rootBlocking) &&
                    rootBlocking.ValueKind == JsonValueKind.True);

            return new SportlinkMatchChangeValidatie(true, meldingen, hasBlocking);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<SportlinkClubResponse<SportlinkMatchDetailsSnapshot>> FetchMatchDetailsSnapshotAsync(
        string publicMatchId, string token, CancellationToken cancellationToken)
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
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(
                    SportlinkClubCallStatus.SportlinkFout, null, "Unauthorized bij match endpoint", 401);

            if (!response.IsSuccessStatusCode)
                return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"Match endpoint gaf {response.StatusCode}", (int)response.StatusCode);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var snapshot = JsonSerializer.Deserialize<SportlinkMatchDetailsSnapshot>(json, JsonOptions);
            if (snapshot == null)
                return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(
                    SportlinkClubCallStatus.SportlinkFout, null, "Match data onvolledig in respons", (int)response.StatusCode);

            return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(SportlinkClubCallStatus.Ok, snapshot, null, (int)response.StatusCode);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialisatie fout voor match-details-snapshot");
            return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(SportlinkClubCallStatus.SportlinkFout, null, "JSON deserialisatie fout", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij match-details-snapshot");
            return new SportlinkClubResponse<SportlinkMatchDetailsSnapshot>(SportlinkClubCallStatus.NetwerkFout, null, "Netwerk fout bij match-details-snapshot", null);
        }
    }

    // ── Rauwe modellen voor UpdateMatchDetails — uitsluitend intern, nooit publiek. Elk veld hier
    // is live bevestigd aanwezig in Sportlinks eigen Match-GET-respons (2026-09-06, #1047),
    // op naam gecontroleerd zonder ooit persoonsgegevens (MatchOfficials) te loggen.
    // Zichtbaarheid `internal` (niet `private`, sinds #995) zodat Planner.Shared.Tests een snapshot
    // rechtstreeks kan opbouwen voor een body-test op BuildMatchChangeRequestBody — dezelfde reden
    // als BuildMatchOfficialsBody hierboven al `internal` is. ──

    internal sealed record SportlinkMatchDetailsSnapshot(
        string? AgeClassCode,
        int? Duration,
        string? MatchStatus,
        [property: JsonConverter(typeof(FlexibleLongJsonConverter))] long? ExternalMatchId,
        SportlinkMatchDateRaw? MatchDate,
        SportlinkFieldRaw? Field,
        SportlinkMatchField? MatchField,
        SportlinkSportRaw? Sport,
        SportlinkTeamsRaw? Teams,
        SportlinkResultRaw? Result,
        SportlinkMatchDetailsFieldsRaw? MatchDetails);

    internal sealed record SportlinkMatchDateRaw(string? Date, string? StartTime);
    // Live vastgesteld (2026-09-06, #1047-vervolg): Field.FieldSize komt in de Match-GET-respons
    // als JSON-getal terug, terwijl UpdateMatchDetails' eigen MatchData.FieldSize als string
    // verwacht wordt (zie de netwerktrace) — zelfde wisselvallige-veldtype-patroon als #1036.
    internal sealed record SportlinkFieldRaw(
        string? FieldId,
        [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? FieldSize,
        int? FieldOffset);
    internal sealed record SportlinkSportRaw(string? IdTag);
    internal sealed record SportlinkTeamRaw(string? TeamName, string? PublicTeamId);
    internal sealed record SportlinkTeamsRaw(SportlinkTeamRaw? Home, SportlinkTeamRaw? Away);
    internal sealed record SportlinkResultRaw(int? HomeScore, int? AwayScore);
    internal sealed record SportlinkEditableFieldRaw(string? Value);
    internal sealed record SportlinkMatchDetailsHomeRaw(SportlinkEditableFieldRaw? AssemblyTime);
    internal sealed record SportlinkMatchDetailsFieldsRaw(
        SportlinkEditableFieldRaw? Description, SportlinkMatchDetailsHomeRaw? MatchDetailsHome);

    // ── Uitgaande envelope voor UpdateMatchDetails (PascalCase = de wire-vorm, geen
    // JsonPropertyName nodig — zelfde patroon als de bestaande PutDressingRoomsAsync/-body's). ──

    private sealed record UpdateMatchDetailsBody(
        object? ConfirmationNeeded,
        bool IsForceUpdate,
        bool IsMatchChangeRequestMandatory,
        bool IsOwnFacility,
        bool IsPlannableByClub,
        bool IsSuccess,
        string PublicApplicantId,
        string PublicMatchId,
        MatchDataBody MatchData);

    private sealed record MatchDataBody(
        string? AgeClassCode,
        string? AssemblyTime,
        int? AwayScore,
        string? AwayTeam,
        string? DepartureTime,
        string? Description,
        string? Drivers,
        int? Duration,
        long? ExternalMatchId,
        string? FacilityId,
        string? FieldId,
        int? FieldOffset,
        string? FieldSize,
        int? HomeScore,
        string? HomeTeam,
        string? MatchChangeRequestRemarks,
        string? MatchDate,
        string? MatchStatus,
        string? PublicAwayTeamId,
        string? PublicHomeTeamId,
        string? SportIdTag,
        string? StartTime);

    /// <summary>
    /// Gedeelde PUT-uitvoering + responsparsing voor alle mutatie-endpoints — derde bijna-identieke
    /// kopie (na #992/#993) was de trigger om ook dit deel te consolideren, zie de doc-comment op
    /// <see cref="ExecuteMutationWithRetryAsync"/>.
    /// </summary>
    private async Task<SportlinkClubResponse<SportlinkMutationResult>> PutMutationAsync(
        string endpoint, string entityName, object body, string token, CancellationToken cancellationToken,
        bool forceDryRun = false,
        Func<string, SportlinkMutationResult, SportlinkMutationResult>? verrijkResultaat = null)
    {
        try
        {
            // Direct na serialisatie (zodat een serialisatiefout alsnog opduikt) en vóór het
            // versturen: dry-run slaat uitsluitend de daadwerkelijke PUT over. Token-refresh en de
            // voorbereidende GETs (snapshot, UserInfo) hebben al plaatsgevonden vóórdat deze methode
            // werd aangeroepen — dat maakt een dry-run realistisch (#998).
            // #994: forceDryRun is een code-niveau lock voor een nog-onbevestigde mutatie (bijv.
            // officials toewijzen) — ONAFHANKELIJK van _isDryRun() (de club-instelling
            // sportlinkDryRun, voor bevestigde mutaties). Ongeacht wat de club instelt, blijft een
            // forceDryRun-aanroep altijd gesimuleerd.
            var serializedBody = JsonSerializer.Serialize(body);
            if (forceDryRun || _isDryRun())
            {
                // NOOIT de body zelf loggen — kan teamnamen/persoonsgegevens bevatten (CISO-regel).
                if (forceDryRun)
                {
                    _logger.LogInformation(
                        "DRY-RUN (code-lock, body niet live bevestigd): {EntityName} niet verzonden naar Sportlink (endpoint {Endpoint}).",
                        entityName, endpoint);
                }
                else
                {
                    _logger.LogInformation(
                        "DRY-RUN: {EntityName} niet verzonden naar Sportlink (endpoint {Endpoint})",
                        entityName, endpoint);
                }
                return new SportlinkClubResponse<SportlinkMutationResult>(
                    SportlinkClubCallStatus.Ok,
                    new SportlinkMutationResult(IsSuccess: true, Violations: null, IsDryRun: true, IsForcedDryRun: forceDryRun),
                    null,
                    200);
            }

            var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
            {
                Content = new StringContent(serializedBody, System.Text.Encoding.UTF8, "application/json")
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

            // Live vastgesteld (2026-09-06, testwedstrijd wedstrijdnummer 69): een door Sportlink
            // afgewezen mutatie geeft HTTP 420 met deze vorm — niet de eerder aangenomen
            // {isSuccess:false, entityViolation:{violations:[{code:...}]}}:
            //   {"Error":true,"Status":"420","Message":"Validation exception : X",
            //    "ViolationCodes":["X","X"],"Violations":{"X":"Nederlandse omschrijving"}}
            // De happy-path-vorm ({"isSuccess":true}) is niet live bevestigd — IsSuccessStatusCode
            // blijft daarom de doorslaggevende factor voor succes, niet het losse veld.
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

            var violations = raw.Violations is { Count: > 0 }
                ? raw.Violations.Select(kv => $"{kv.Key}: {kv.Value}").ToList()
                : raw.ViolationCodes;
            var isSuccess = raw.Error != true && response.IsSuccessStatusCode;
            var mutationResult = new SportlinkMutationResult(isSuccess, violations);
            if (verrijkResultaat != null)
                mutationResult = verrijkResultaat(json, mutationResult);
            return new SportlinkClubResponse<SportlinkMutationResult>(
                SportlinkClubCallStatus.Ok, mutationResult, null, (int)response.StatusCode);
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
    // (opgeschoonde Violations-lijst), niet deze rauwe vorm. Live vastgesteld op een afgewezen
    // UpdateMatchDressingRooms-aanroep (2026-09-06, wedstrijdnummer 69) — zie het commentaar bij
    // de aanroepplek hierboven voor het exacte, geobserveerde JSON-voorbeeld.
    private sealed record SportlinkMutationResultRaw(
        bool? Error,
        string? Status,
        string? Message,
        List<string>? ViolationCodes,
        Dictionary<string, string>? Violations);

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
                var items = UnwrapArrayEnvelope(doc.RootElement, "Matches", "matches");

                if (items is not { ValueKind: JsonValueKind.Array } arrayItems)
                    return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                        SportlinkClubCallStatus.SportlinkFout, null, "MatchProgramOverview-respons had onverwachte vorm", (int)response.StatusCode);

                entries = JsonSerializer.Deserialize<List<SportlinkMatchProgramEntry>>(arrayItems.GetRawText(), JsonOptions);
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

    /// <summary>
    /// Sportlink-responsen zijn soms een kale JSON-array en soms genest onder een envelope-property
    /// (naam en casing per endpoint verschillend, niet 100% live bevestigd) — deze helper zoekt de
    /// array op één van beide manieren zodat elke fetch-methode niet zijn eigen kopie hoeft te houden.
    /// </summary>
    private static JsonElement? UnwrapArrayEnvelope(JsonElement root, params string[] propertyNames)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;

        foreach (var name in propertyNames)
        {
            if (root.TryGetProperty(name, out var property))
                return property;
        }

        return null;
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
