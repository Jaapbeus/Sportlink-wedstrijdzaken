using Microsoft.Extensions.Logging;
using System.Text.Json;
using static Planner.Shared.Integrations.SportlinkClub.SportlinkClubHttp;
using static Planner.Shared.Integrations.SportlinkClub.SportlinkJsonHulp;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Read-only wedstrijdaanroepen: match, programma, veldplanner en wijzigingsverzoeken (#1631, verplaatst uit SportlinkClubClient).</summary>
internal sealed class SportlinkWedstrijdLezer
{
    private readonly SportlinkClubTransport _transport;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    internal SportlinkWedstrijdLezer(SportlinkClubTransport transport)
    {
        _transport = transport;
        _httpClient = transport.Http;
        _logger = transport.Logger;
    }

    private const string MatchProgramOverviewEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/MatchProgramOverview";

    private const string FacilityOccupationEndpoint =
        "https://club.sportlink.com/navajo/entity/common/clubweb/competition/facilityoccupation/FacilityOccupation";

    private const string MatchChangeRequestsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/changerequest/MatchChangeRequests";

    public async Task<SportlinkClubResponse<SportlinkMatch>> GetMatchAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
    {
        return await _transport.ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchMatchAsync(publicMatchId, token, functioneleRol, ct), cancellationToken);
    }

    /// <summary>Zelfde token-refresh/401-eenmalige-retry-patroon als <see cref="GetMatchAsync"/>,
    /// maar zonder deserialisatie — zie <see cref="ISportlinkClubClient.GetMatchRawJsonAsync"/>.</summary>
    public async Task<SportlinkClubResponse<string>> GetMatchRawJsonAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
    {
        return await _transport.ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchMatchRawJsonAsync(publicMatchId, token, ct), cancellationToken);
    }

    private async Task<SportlinkClubResponse<string>> FetchMatchRawJsonAsync(
        string publicMatchId, string token, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _transport.GetMatchEndpointResponseAsync(publicMatchId, token, cancellationToken);

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
        return await _transport.ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchMatchProgramOverviewRawAsync(datum, token, ct), cancellationToken);
    }

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

    private async Task<SportlinkClubResponse<SportlinkMatch>> FetchMatchAsync(
        string publicMatchId,
        string token,
        string functioneleRol,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _transport.GetMatchEndpointResponseAsync(publicMatchId, token, cancellationToken);

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

    public Task<SportlinkClubResponse<IReadOnlyList<SportlinkVeldplannerBlok>>> GetVeldplannerAsync(
        string functioneleRol, string facilityId, DateOnly datum, CancellationToken cancellationToken = default)
        => _transport.ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
            (token, ct) => FetchVeldplannerAsync(facilityId, datum, token, ct), cancellationToken);

    private async Task<SportlinkClubResponse<IReadOnlyList<SportlinkVeldplannerBlok>>> FetchVeldplannerAsync(
        string facilityId, DateOnly datum, string token, CancellationToken cancellationToken)
    {
        const string entityName = "competition/facilityoccupation/FacilityOccupation";
        var url = $"{FacilityOccupationEndpoint}?FacilityId={Uri.EscapeDataString(facilityId)}" +
                  $"&GameDate={datum:yyyy-MM-dd}&IsSeasonStartAllowed=false";
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ZetSportlinkHeaders(request, entityName, token);
            var response = await VerstuurMetTimeoutAsync(ct => _httpClient.SendAsync(request, ct), cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return Fout(SportlinkClubCallStatus.SportlinkFout, $"Unauthorized bij {entityName} endpoint", 401);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Entity} endpoint gaf {StatusCode}", entityName, response.StatusCode);
                return Fout(SportlinkClubCallStatus.SportlinkFout, $"{entityName} endpoint gaf {response.StatusCode}", (int)response.StatusCode);
            }

            var blokken = SportlinkVeldplannerParser.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return new SportlinkClubResponse<IReadOnlyList<SportlinkVeldplannerBlok>>(
                SportlinkClubCallStatus.Ok, blokken, null, (int)response.StatusCode);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialisatie fout voor {Entity} endpoint", entityName);
            return Fout(SportlinkClubCallStatus.SportlinkFout, "JSON deserialisatie fout", null);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "{Entity} endpoint timeout", entityName);
            return Fout(SportlinkClubCallStatus.NetwerkFout, $"Timeout bij {entityName} endpoint", null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "{Entity} endpoint netwerk fout", entityName);
            return Fout(SportlinkClubCallStatus.NetwerkFout, $"Netwerk fout bij {entityName} endpoint", null);
        }

        static SportlinkClubResponse<IReadOnlyList<SportlinkVeldplannerBlok>> Fout(
            SportlinkClubCallStatus status, string melding, int? http) => new(status, null, melding, http);
    }

    public async Task<SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>> GetChangeRequestsAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default)
    {
        return await _transport.ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
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
}
