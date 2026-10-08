using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Read-only veldplanner-aanroep (#1563) — alleen GET, nooit een mutatie.</summary>
public partial class SportlinkClubClient
{
    private const string FacilityOccupationEndpoint =
        "https://club.sportlink.com/navajo/entity/common/clubweb/competition/facilityoccupation/FacilityOccupation";

    public Task<SportlinkClubResponse<IReadOnlyList<SportlinkVeldplannerBlok>>> GetVeldplannerAsync(
        string functioneleRol, string facilityId, DateOnly datum, CancellationToken cancellationToken = default)
        => ExecuteWithTokenRetryAsync(functioneleRol, RetryBeleid.Lezen,
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
}
