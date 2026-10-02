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

    // #1440: verwijderen van een clubwedstrijd. Methode (DELETE), parameter (PublicMatchId als
    // querystring, geen body) en foutvorm (HTTP 420 met Violations) komen uit Sportlinks PUBLIEKE
    // frontend-bundle (02-10-2026) — NOOIT live gezien. Of verwijderen in Sportlink terug te draaien
    // is, is onbekend. Daarom hard op forceDryRun totdat een mens (nooit een agent, zie
    // docs/SPORTLINK-WEB-EXTENSION.md §4.4) een live trace heeft gedaan en deze constante in een
    // aparte, reviewbare PR op true zet. Grep-baar bij naam; SportlinkClubMatchDeleteTests bewaakt hem.
    private const bool ClubMatchDeleteLiveBevestigd = false;

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

    public Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateDressingRoomsAsync(
        string functioneleRol,
        string publicMatchId,
        string? homeDressingRoomId,
        string? awayDressingRoomId,
        string? officialDressingRoomId,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
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
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
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
    /// <c>PUT competition/match/UpdateMatchDetails</c>. Dit is de enige Sportlink-mutatie die een
    /// ECHTE tegenstander raakt. <b>Sinds #1319</b> heeft de eigenaar
    /// <see cref="UpdateMatchDetailsChangeRequestLiveBevestigd"/> op <c>true</c> gezet na een live
    /// netwerktrace — de aanroep volgt vanaf nu de gewone club-instelling <c>sportlinkDryRun</c>,
    /// net als elke andere bevestigde mutatie.
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
    /// Bij <c>Status=Ok</c>: <c>Data.Mutatie.IsDryRun</c> volgt de club-instelling
    /// <c>sportlinkDryRun</c>. Alleen als die simuleert is <c>Data.Validatie</c> <c>null</c> (geen
    /// echte Sportlink-respons om te parsen); anders bevat het de geparste <c>ConfirmationNeeded</c>-
    /// envelope van de echte respons.
    /// </returns>
    public Task<SportlinkClubResponse<SportlinkMatchChangeRequestResult>> RequestMatchChangeAsync(
        string functioneleRol,
        string publicMatchId,
        DateOnly? nieuweDatum,
        TimeOnly? nieuweStartTijd,
        string? nieuweFacilityId,
        string toelichting,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync<SportlinkMatchChangeRequestResult>(
            functioneleRol,
            RetryBeleid.Mutatie,
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
        return await ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchChangeRequestsAsync(token, ct), cancellationToken);
    }

    private async Task<SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>> FetchChangeRequestsAsync(
        string token, CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, MatchChangeRequestsEndpoint);
            ZetSportlinkHeaders(request, "competition/match/changerequest/MatchChangeRequests", token);

            var response = await VerstuurMetTimeoutAsync(ct => _httpClient.SendAsync(request, ct), cancellationToken);

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
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
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
            ZetSportlinkHeaders(request, "user/UserInfo", token);

            var response = await VerstuurMetTimeoutAsync(ct => _httpClient.SendAsync(request, ct), cancellationToken);
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
    /// <c>PUT competition/match/official/MatchOfficialsAction</c>. <b>Sinds #1319</b> heeft de
    /// eigenaar <see cref="MatchOfficialsActionLiveBevestigd"/> op <c>true</c> gezet na een live
    /// netwerktrace — de aanroep volgt vanaf nu de gewone club-instelling <c>sportlinkDryRun</c>,
    /// net als elke andere bevestigde mutatie. Zie <see cref="SportlinkOfficialToewijzing"/> voor de
    /// aannames op elementniveau.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkMutationResult>> AssignOfficialsAsync(
        string functioneleRol,
        string publicMatchId,
        IReadOnlyList<SportlinkOfficialToewijzing> officials,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
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
    /// zodat de vorm (#994: "OfficialPosition"/"PersoonId" als veldnamen binnen elk element van
    /// <c>OfficialsToBeAssigned</c> — zie <see cref="SportlinkOfficialToewijzing"/>, sinds #1319 live
    /// bevestigd) direct getest kan worden zonder een echte PUT te versturen.
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
    /// Maakt een nieuwe oefenwedstrijd aan (#997) — zie <see cref="ISportlinkClubClient.CreateClubMatchAsync"/>.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkMutationResult>> CreateClubMatchAsync(
        string functioneleRol,
        SportlinkClubMatchAanvraag aanvraag,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            (token, ct) => PostClubMatchAsync(aanvraag, token, ct),
            cancellationToken);

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PostClubMatchAsync(
        SportlinkClubMatchAanvraag aanvraag, string token, CancellationToken cancellationToken)
    {
        return PutMutationAsync(
            ClubMatchEndpoint,
            "competition/match/clubmatch/ClubMatch",
            BuildClubMatchBody(aanvraag),
            token,
            cancellationToken,
            forceDryRun: !ClubMatchLiveBevestigd,
            method: HttpMethod.Post);
    }

    /// <summary>
    /// Verwijdert een clubwedstrijd (#1440) — zie <see cref="ISportlinkClubClient.DeleteClubMatchAsync"/>.
    /// Zolang <see cref="ClubMatchDeleteLiveBevestigd"/> <c>false</c> is, verlaat er geen DELETE deze
    /// client: <see cref="PutMutationAsync"/> slaat het verzenden over (forceDryRun) en meldt
    /// <c>IsForcedDryRun</c>.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkMutationResult>> DeleteClubMatchAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            (token, ct) => PutMutationAsync(
                BouwClubMatchDeleteUrl(publicMatchId),
                "competition/match/clubmatch/ClubMatchDelete",
                body: null,
                token,
                ct,
                forceDryRun: !ClubMatchDeleteLiveBevestigd,
                method: HttpMethod.Delete,
                legeSuccesBodyIsSucces: true),
            cancellationToken);

    /// <summary>URL van de verwijderaanroep: <c>PublicMatchId</c> als queryparameter, zoals Sportlinks
    /// eigen frontend hem meestuurt (<c>params:{PublicMatchId}</c> in de bundle, #1440).</summary>
    internal static string BouwClubMatchDeleteUrl(string publicMatchId)
        => ClubMatchDeleteEndpoint + "?PublicMatchId=" + Uri.EscapeDataString(publicMatchId);

    /// <summary>
    /// #1440: een 2xx zonder body is voor een verwijderaanroep een geslaagde mutatie. Sportlinks eigen
    /// frontend leest de succesbody van <c>ClubMatchDelete</c> niet; zonder deze uitzondering zou een
    /// lege respons als "onherkenbare respons" (fout) gelden terwijl de wedstrijd wél weg is.
    /// </summary>
    internal static bool IsLegeSuccesRespons(System.Net.HttpStatusCode status, string body)
        => (int)status is >= 200 and <= 299 && string.IsNullOrWhiteSpace(body);

    /// <summary>
    /// Bouwt de <c>ClubMatch</c>-requestbody. <b>Sinds #1427 live bevestigd</b>: veld voor veld
    /// gelijk aan wat Sportlink Clubs eigen formulier verstuurt (console-trace 01-10-2026, HTTP 200
    /// met nieuw <c>PublicMatchId</c>). Datum en tijd gaan apart, het eigen team staat als thuis- én
    /// uit-ID, en de teamnamen zijn vrije tekst.
    /// </summary>
    internal static object BuildClubMatchBody(SportlinkClubMatchAanvraag aanvraag) =>
        new
        {
            AwayTeam = aanvraag.AwayTeam,
            HomeTeam = aanvraag.HomeTeam,
            PublicAwayTeamId = aanvraag.PublicTeamId,
            PublicHomeTeamId = aanvraag.PublicTeamId,
            AgeClassCode = aanvraag.AgeClassCode,
            AwayResult = -1,
            Description = aanvraag.Description,
            Duration = aanvraag.Duration,
            ExternalMatchId = aanvraag.ExternalMatchId,
            HomeResult = -1,
            MatchDate = aanvraag.MatchDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            SportIdTag = aanvraag.SportIdTag,
            StartTime = aanvraag.StartTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            IsHomeMatch = aanvraag.IsHomeMatch,
            FieldOffset = aanvraag.FieldOffset,
            FacilityId = aanvraag.FacilityId,
            SubFacilityId = aanvraag.SubFacilityId,
            FieldSize = aanvraag.FieldSize
        };

    /// <summary>
    /// Haalt de vier lijsten op waarmee Sportlinks eigen formulier een oefenwedstrijd opbouwt (#1427)
    /// — zie <see cref="ISportlinkClubClient.GetClubMatchContextAsync"/>. Read-only.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkClubMatchContext>> GetClubMatchContextAsync(
        string functioneleRol, CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchClubMatchContextAsync(token, ct), cancellationToken);

    private async Task<SportlinkClubResponse<SportlinkClubMatchContext>> FetchClubMatchContextAsync(
        string token, CancellationToken cancellationToken)
    {
        var defaults = await FetchClubMatchJsonAsync<SportlinkClubMatchDefaults>(ClubMatchDefaultsEndpoint, "ClubMatchDefaults", token, cancellationToken);
        if (defaults.Status != SportlinkClubCallStatus.Ok) return Doorgeven(defaults);
        var teams = await FetchClubMatchJsonAsync<ClubTeamsRaw>(PickListsTeamsEndpoint, "PickListsTeams", token, cancellationToken);
        if (teams.Status != SportlinkClubCallStatus.Ok) return Doorgeven(teams);
        var locaties = await FetchClubMatchJsonAsync<FacilitiesRaw>(PickListsLocationEndpoint + "?SearchClubId=", "PickListsLocation", token, cancellationToken);
        if (locaties.Status != SportlinkClubCallStatus.Ok) return Doorgeven(locaties);
        var info = await FetchClubMatchJsonAsync<MatchInformationRaw>(PickListsMatchInformationEndpoint, "PickListsMatchInformation", token, cancellationToken);
        if (info.Status != SportlinkClubCallStatus.Ok) return Doorgeven(info);

        return new SportlinkClubResponse<SportlinkClubMatchContext>(
            SportlinkClubCallStatus.Ok,
            new SportlinkClubMatchContext(
                defaults.Data!,
                teams.Data!.ClubTeams ?? new List<SportlinkClubTeam>(),
                locaties.Data!.Facilities ?? new List<SportlinkClubFacility>(),
                info.Data!.Activities ?? new List<SportlinkClubActivity>(),
                info.Data.AgeClasses ?? new List<SportlinkClubAgeClass>()),
            null, 200);

        static SportlinkClubResponse<SportlinkClubMatchContext> Doorgeven<T>(SportlinkClubResponse<T> fout) where T : class =>
            new(fout.Status, null, fout.FoutmeldingVoorLog, fout.HttpStatusCode);
    }

    private sealed record ClubTeamsRaw(List<SportlinkClubTeam>? ClubTeams);
    private sealed record FacilitiesRaw(List<SportlinkClubFacility>? Facilities);
    private sealed record MatchInformationRaw(List<SportlinkClubActivity>? Activities, List<SportlinkClubAgeClass>? AgeClasses);

    /// <summary>Eén read-only clubmatch-GET met getypeerde deserialisatie (#1427). Logt bij een
    /// fout alleen entity en status, nooit de body.</summary>
    private async Task<SportlinkClubResponse<T>> FetchClubMatchJsonAsync<T>(
        string endpoint, string entityKort, string token, CancellationToken cancellationToken) where T : class
    {
        var entityName = "competition/match/clubmatch/" + entityKort;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            ZetSportlinkHeaders(request, entityName, token);
            var response = await VerstuurMetTimeoutAsync(ct => _httpClient.SendAsync(request, ct), cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<T>(SportlinkClubCallStatus.SportlinkFout, null, $"Unauthorized bij {entityName} endpoint", 401);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Entity} endpoint gaf {StatusCode}", entityName, response.StatusCode);
                return new SportlinkClubResponse<T>(SportlinkClubCallStatus.SportlinkFout, null, $"{entityName} endpoint gaf {response.StatusCode}", (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var data = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return data == null
                ? new SportlinkClubResponse<T>(SportlinkClubCallStatus.SportlinkFout, null, $"{entityName}-respons was leeg", (int)response.StatusCode)
                : new SportlinkClubResponse<T>(SportlinkClubCallStatus.Ok, data, null, (int)response.StatusCode);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialisatie fout voor {Entity} endpoint", entityName);
            return new SportlinkClubResponse<T>(SportlinkClubCallStatus.SportlinkFout, null, "JSON deserialisatie fout", null);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "{Entity} endpoint timeout", entityName);
            return new SportlinkClubResponse<T>(SportlinkClubCallStatus.NetwerkFout, null, $"Timeout bij {entityName} endpoint", null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "{Entity} endpoint netwerk fout", entityName);
            return new SportlinkClubResponse<T>(SportlinkClubCallStatus.NetwerkFout, null, $"Netwerk fout bij {entityName} endpoint", null);
        }
    }

    /// <summary>
    /// Haalt de twee ondersteunende picklists op (#997) — zie
    /// <see cref="ISportlinkClubClient.GetClubMatchPickListsAsync"/>. Zelfde token-refresh/
    /// 401-eenmalige-retry-patroon als de overige read-only methodes in deze klasse.
    /// </summary>
    public async Task<SportlinkClubResponse<SportlinkClubMatchPickLists>> GetClubMatchPickListsAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchClubMatchPickListsAsync(token, ct), cancellationToken);
    }

    private async Task<SportlinkClubResponse<SportlinkClubMatchPickLists>> FetchClubMatchPickListsAsync(
        string token, CancellationToken cancellationToken)
    {
        // #1427: de teamlijst mag de locatielijst niet meer blokkeren. Het aanmaakpad gebruikt
        // alleen de locaties (FacilityId); de teams komen uit onze eigen database. Live gaf
        // PickListsTeams HTTP 200 met een onherkende vorm, waardoor PickListsLocation nooit werd
        // aangeroepen en FacilityId altijd leeg bleef. Een mislukte teamlijst wordt nu een lege lijst.
        var teamsResult = await FetchPickListAsync(
            PickListsTeamsEndpoint, "competition/match/clubmatch/PickListsTeams", token, cancellationToken);

        var locationsResult = await FetchPickListAsync(
            PickListsLocationEndpoint, "competition/match/clubmatch/PickListsLocation", token, cancellationToken);
        if (locationsResult.Status != SportlinkClubCallStatus.Ok)
            return new SportlinkClubResponse<SportlinkClubMatchPickLists>(
                locationsResult.Status, null, locationsResult.FoutmeldingVoorLog, locationsResult.HttpStatusCode);

        return new SportlinkClubResponse<SportlinkClubMatchPickLists>(
            SportlinkClubCallStatus.Ok,
            new SportlinkClubMatchPickLists(
                teamsResult.Data ?? new List<SportlinkPickListItem>(),
                locationsResult.Data ?? new List<SportlinkPickListItem>()),
            null,
            200);
    }

    /// <summary>
    /// Rauwe fetch voor één picklist-endpoint — ONBEVESTIGD qua respons-vorm (kale array, of genest
    /// onder een envelope-property), zelfde defensieve aanpak als
    /// <see cref="FetchMatchProgramOverviewRawAsync"/>/<see cref="FetchChangeRequestsAsync"/>.
    /// </summary>
    private async Task<SportlinkClubResponse<IReadOnlyList<SportlinkPickListItem>>> FetchPickListAsync(
        string endpoint, string entityName, string token, CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            ZetSportlinkHeaders(request, entityName, token);

            var response = await VerstuurMetTimeoutAsync(ct => _httpClient.SendAsync(request, ct), cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<IReadOnlyList<SportlinkPickListItem>>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"Unauthorized bij {entityName} endpoint", 401);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Entity} endpoint gaf {StatusCode}", entityName, response.StatusCode);
                return new SportlinkClubResponse<IReadOnlyList<SportlinkPickListItem>>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"{entityName} endpoint gaf {response.StatusCode}", (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var element = UnwrapArrayEnvelope(doc.RootElement, "Items", "Teams", "Locations", "Data")
                    ?? EnigeArrayProperty(doc.RootElement);

                if (element is not { ValueKind: JsonValueKind.Array } arrayElement)
                {
                    // #1427: alleen de STRUCTUUR loggen (propertynamen + JSON-soorten), nooit
                    // waarden — zo is de echte vorm uit de log af te leiden zonder netwerktrace.
                    _logger.LogWarning("{Entity}-respons had onverwachte vorm: {Structuur}",
                        entityName, BeschrijfJsonStructuur(doc.RootElement));
                    return new SportlinkClubResponse<IReadOnlyList<SportlinkPickListItem>>(
                        SportlinkClubCallStatus.SportlinkFout, null, $"{entityName}-respons had onverwachte vorm", (int)response.StatusCode);
                }

                var items = arrayElement.EnumerateArray().Select(ParsePickListItem).ToList();
                return new SportlinkClubResponse<IReadOnlyList<SportlinkPickListItem>>(
                    SportlinkClubCallStatus.Ok, items, null, (int)response.StatusCode);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "JSON deserialisatie fout voor {Entity} endpoint", entityName);
                return new SportlinkClubResponse<IReadOnlyList<SportlinkPickListItem>>(
                    SportlinkClubCallStatus.SportlinkFout, null, "JSON deserialisatie fout", (int)response.StatusCode);
            }
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "{Entity} endpoint timeout", entityName);
            return new SportlinkClubResponse<IReadOnlyList<SportlinkPickListItem>>(SportlinkClubCallStatus.NetwerkFout, null, $"Timeout bij {entityName} endpoint", null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "{Entity} endpoint netwerk fout", entityName);
            return new SportlinkClubResponse<IReadOnlyList<SportlinkPickListItem>>(SportlinkClubCallStatus.NetwerkFout, null, $"Netwerk fout bij {entityName} endpoint", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onverwachte fout bij {Entity} endpoint", entityName);
            return new SportlinkClubResponse<IReadOnlyList<SportlinkPickListItem>>(SportlinkClubCallStatus.SportlinkFout, null, $"Onverwachte fout bij {entityName} endpoint", null);
        }
    }

    /// <summary>
    /// ONBEVESTIGD: welke JSON-veldnamen een picklist-item daadwerkelijk gebruikt is nooit met een
    /// netwerktrace gezien — probeert daarom een paar aannemelijke namen per waarde, in volgorde
    /// van waarschijnlijkheid, in plaats van een strikt contract af te dwingen dat mogelijk meteen
    /// breekt op de eerste live respons.
    /// </summary>
    internal static SportlinkPickListItem ParsePickListItem(JsonElement item)
    {
        var id = FirstStringProperty(item, "Id", "PublicTeamId", "PublicLocationId", "FacilityId", "Value", "Code");
        var naam = FirstStringProperty(item, "Name", "TeamName", "NormalizedName", "LocationName", "FacilityName", "Text", "Description", "Naam");
        return new SportlinkPickListItem(id, naam);
    }

    private static string? FirstStringProperty(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }

        return null;
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

    // #995, Aanpak-stap 1: de eigenaar heeft de body van beide PUT's en de bevestigingsvlag
    // live vastgelegd (#1319) — dit blijft niettemin uitsluitend stap 1 (valideren); stap 2
    // (bevestigen) is een bewuste, aparte scope-beslissing en wordt hier niet gebouwd.
    /// <summary>
    /// Bouwt en verstuurt de <c>UpdateMatchDetails</c>-envelope voor een datum/tijd/accommodatie-
    /// wijzigingsverzoek (#995) — bewust GEEN parametrisering van <see cref="PutMatchDetailsAsync"/>
    /// (#993's live-bevestigde veld-wijzigingspad), zie de doc-comment op
    /// <see cref="RequestMatchChangeAsync"/>. Sinds #1319 volgt deze aanroep de gewone
    /// club-instelling <c>sportlinkDryRun</c> (zie <see cref="UpdateMatchDetailsChangeRequestLiveBevestigd"/>):
    /// bij een echte PUT loopt de respons door <see cref="ParseMatchChangeValidatie"/> via de
    /// <c>verrijkResultaat</c>-hook; bij een gesimuleerde (dry-run) aanroep gebeurt dat niet, want
    /// die hook loopt pas ná een echte HTTP-respons.
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

            // #1320: vier toplevel booleans, gezien bij de oefenwedstrijd-trace van 2026-09-26 —
            // GEEN bewijs voor de verplichte-wijzigingsverzoek-vorm, zie de doc-comment op
            // SportlinkMatchChangeValidatie. Ontbrekend of geen boolean -> null, nooit gokken.
            bool? LeesToplevelBool(string naam) =>
                doc.RootElement.TryGetProperty(naam, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? el.GetBoolean()
                    : null;

            var isSuccess = LeesToplevelBool("IsSuccess");
            var isMatchChangeRequestMandatory = LeesToplevelBool("IsMatchChangeRequestMandatory");
            var isOwnFacility = LeesToplevelBool("IsOwnFacility");
            var isForceUpdate = LeesToplevelBool("IsForceUpdate");

            if (!doc.RootElement.TryGetProperty("ConfirmationNeeded", out var confirmationElement) ||
                confirmationElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new SportlinkMatchChangeValidatie(
                    false, Array.Empty<string>(), false,
                    isSuccess, isMatchChangeRequestMandatory, isOwnFacility, isForceUpdate);

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

            return new SportlinkMatchChangeValidatie(
                true, meldingen, hasBlocking,
                isSuccess, isMatchChangeRequestMandatory, isOwnFacility, isForceUpdate);
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
            var response = await GetMatchEndpointResponseAsync(publicMatchId, token, cancellationToken);
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
    /// Gedeelde verzenduitvoering + responsparsing voor alle mutatie-endpoints — derde bijna-
    /// identieke kopie (na #992/#993) was de trigger om ook dit deel te consolideren, zie de
    /// doc-comment op <see cref="ExecuteWithTokenRetryAsync"/>. Sinds #997 ook voor POST (zie
    /// <paramref name="method"/>) — de drie bestaande PUT-aanroepen (#992/#993/#996) en de PUT van
    /// #994 geven <paramref name="method"/> niet mee en blijven dus ongewijzigd <c>HttpMethod.Put</c>
    /// gebruiken (default), alleen #997's <c>CreateClubMatchAsync</c> geeft expliciet
    /// <c>HttpMethod.Post</c> mee. De naam <c>PutMutationAsync</c> is bewust NIET hernoemd — dat zou
    /// alle bestaande call sites moeten aanraken voor een verandering die zuiver optioneel is, meer
    /// regressierisico op de drie bevestigde PUT-paden dan een simpele parameter-toevoeging.
    /// </summary>
    private async Task<SportlinkClubResponse<SportlinkMutationResult>> PutMutationAsync(
        string endpoint, string entityName, object? body, string token, CancellationToken cancellationToken,
        bool forceDryRun = false,
        Func<string, SportlinkMutationResult, SportlinkMutationResult>? verrijkResultaat = null,
        HttpMethod? method = null, bool legeSuccesBodyIsSucces = false)
    {
        var httpMethod = method ?? HttpMethod.Put;
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
            // #1440: body == null (DELETE) → geen content; de parameters staan dan in de URL.
            var serializedBody = body == null ? null : JsonSerializer.Serialize(body);
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

            var request = new HttpRequestMessage(httpMethod, endpoint);
            if (serializedBody != null)
                request.Content = new StringContent(serializedBody, System.Text.Encoding.UTF8, "application/json");
            ZetSportlinkHeaders(request, entityName, token);

            var response = await VerstuurMetTimeoutAsync(ct => _httpClient.SendAsync(request, ct), cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<SportlinkMutationResult>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"Unauthorized bij {entityName} endpoint", 401);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (legeSuccesBodyIsSucces && IsLegeSuccesRespons(response.StatusCode, json))
                return new SportlinkClubResponse<SportlinkMutationResult>(
                    SportlinkClubCallStatus.Ok, new SportlinkMutationResult(true, null), null, (int)response.StatusCode);

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

            // #1417: een 5xx is een serverfout van Sportlink, geen inhoudelijke afwijzing — ook als
            // de body toevallig als JSON parseert. Vóór deze fix kwam zo'n respons terug als
            // Status=Ok/IsSuccess=false, terwijl een niet-JSON 5xx al SportlinkFout gaf; dezelfde
            // fout werd dus per toeval anders geclassificeerd. Uniform: altijd SportlinkFout mét
            // statuscode, zodat SportlinkEndpointCore.VertaalStatusNaarFout één pad kent.
            if ((int)response.StatusCode is >= 500 and <= 599)
            {
                _logger.LogWarning("{Entity} endpoint gaf serverfout {StatusCode}", entityName, response.StatusCode);
                return new SportlinkClubResponse<SportlinkMutationResult>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"{entityName} endpoint gaf serverfout {(int)response.StatusCode}", (int)response.StatusCode);
            }

            var violations = raw.Violations is { Count: > 0 }
                ? raw.Violations.Select(kv => $"{kv.Key}: {kv.Value}").ToList()
                : raw.ViolationCodes;
            var isSuccess = raw.Error != true && response.IsSuccessStatusCode;
            // #1427: een afwijzing zonder Violations (live gezien: HTTP 602 op ClubMatch) toonde in
            // de GUI alleen "afgewezen". Sportlinks eigen Message geeft dan de reden — die gaat
            // naar de gebruiker; in de log alleen de statuscode, nooit de body.
            if (!isSuccess && violations is not { Count: > 0 })
            {
                _logger.LogWarning("{Entity} endpoint wees af met {StatusCode} zonder violations", entityName, (int)response.StatusCode);
                violations = new List<string> { $"Sportlink {(int)response.StatusCode}: {raw.Message ?? "geen foutmelding"}" };
            }
            // #997: PublicMatchId komt alleen terug op de ClubMatch-aanmaak-respons — voor elke
            // andere mutatie-respons (dressing rooms, veld, officials, change-request-actie) staat
            // dit veld hier niet in en blijft raw.PublicMatchId dus null (bestaand gedrag ongewijzigd).
            var mutationResult = new SportlinkMutationResult(isSuccess, violations, PublicMatchId: raw.PublicMatchId);
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
        Dictionary<string, string>? Violations,
        // #997: alleen aanwezig op de ClubMatch-aanmaak-respons ({"PublicMatchId":"M...","IsSuccess":true})
        // — optioneel, dus geen effect op de bestaande #992/#993/#994/#996-mutatieresponsen.
        string? PublicMatchId);

    private async Task<SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>> FetchMatchProgramOverviewRawAsync(
        DateOnly datum, string token, CancellationToken cancellationToken)
    {
        try
        {
            var datumStr = datum.ToString("yyyy-MM-dd");
            var url = $"{MatchProgramOverviewEndpoint}?DateFrom={datumStr}&DateTo={datumStr}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ZetSportlinkHeaders(request, "competition/match/MatchProgramOverview", token);

            // #1387: dit endpoint is gedocumenteerd traag (12+s) — eigen, ruimere timeout in plaats
            // van DefaultCallTimeout, zie ReverseLookupCallTimeout.
            var response = await VerstuurMetTimeoutAsync(ct => _httpClient.SendAsync(request, ct), ReverseLookupCallTimeout, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>(
                    SportlinkClubCallStatus.SportlinkFout, null, "Unauthorized bij MatchProgramOverview endpoint", 401);

            if (!response.IsSuccessStatusCode)
            {
                // Alleen de status, nooit de body: die kan wedstrijd-/teamgegevens bevatten (CISO-regel, #1122).
                _logger.LogWarning("MatchProgramOverview endpoint gaf {StatusCode}", response.StatusCode);
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
    /// <summary>
    /// #1427: als een object precies één array-property heeft, is dat vrijwel zeker de lijst —
    /// ongeacht hoe Sportlink die property noemt. Meerdere arrays → <c>null</c>: niet gokken.
    /// </summary>
    internal static JsonElement? EnigeArrayProperty(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var arrays = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).ToList();
        return arrays.Count == 1 ? arrays[0].Value : null;
    }

    /// <summary>
    /// #1427: beschrijft de vorm van een JSON-respons zonder één waarde te tonen — bijv.
    /// <c>Object{Foo:Array[12],Bar:String}</c>. Veilig om te loggen; maximaal tien properties.
    /// </summary>
    internal static string BeschrijfJsonStructuur(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Object => "Object{" + string.Join(",", root.EnumerateObject().Take(10).Select(p =>
            p.Value.ValueKind == JsonValueKind.Array ? $"{p.Name}:Array[{p.Value.GetArrayLength()}]" : $"{p.Name}:{p.Value.ValueKind}")) + "}",
        JsonValueKind.Array => $"Array[{root.GetArrayLength()}]",
        _ => root.ValueKind.ToString()
    };

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

    private async Task<SportlinkClubResponse<SportlinkMatch>> FetchMatchAsync(
        string publicMatchId,
        string token,
        string functioneleRol,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await GetMatchEndpointResponseAsync(publicMatchId, token, cancellationToken);

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

            // Alleen de status, nooit de body (CISO-regel, #1122).
            _logger.LogWarning("Match endpoint gaf {StatusCode}", response.StatusCode);
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

    /// <summary>De drie Navajo-headers + Bearer-token die élke Sportlink Club-aanroep draagt
    /// (docs/SPORTLINK-WEB-EXTENSION.md §6.2) — één plek in plaats van acht kopieën (#1122).</summary>
    private static void ZetSportlinkHeaders(HttpRequestMessage request, string entityName, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Navajo-Entity", entityName);
        request.Headers.Add("X-Navajo-Instance", "KNVB");
        request.Headers.Add("X-Navajo-Locale", "nl");
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
