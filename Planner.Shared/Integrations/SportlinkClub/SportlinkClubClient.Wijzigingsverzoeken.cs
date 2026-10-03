using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Read-only wijzigingsverzoek-aanroepen (#1493) — zelfde klasse, alleen verplaatst. Geen gedragswijziging.</summary>
public partial class SportlinkClubClient
{
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
}
