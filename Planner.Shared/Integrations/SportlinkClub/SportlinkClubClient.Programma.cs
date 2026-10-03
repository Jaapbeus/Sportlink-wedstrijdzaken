using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Wedstrijdprogramma-aanroepen en JSON-envelope-helpers (#1493) — zelfde klasse, alleen verplaatst. Geen gedragswijziging.</summary>
public partial class SportlinkClubClient
{
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
}
