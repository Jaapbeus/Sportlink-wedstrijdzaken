using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Read-only clubwedstrijd-aanroepen (context en keuzelijsten) (#1493) — zelfde klasse, alleen verplaatst. Geen gedragswijziging.</summary>
public partial class SportlinkClubClient
{
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
}
