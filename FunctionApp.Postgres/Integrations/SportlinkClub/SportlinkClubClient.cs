using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Integrations.SportlinkClub;

/// <summary>
/// Read-only client voor de club.sportlink.com Navajo-API (#991, epic #986). Kale
/// <see cref="HttpClient"/> zonder retry-library — er bestaat nergens in deze repo al een
/// Polly-afhankelijkheid, en de bestaande Sportlink-sync (<c>Sync/PostgresSyncPipeline.cs</c>)
/// gebruikt zelf ook een kale <c>HttpClient</c>. Uitsluitend GET-aanroepen; geen enkele
/// mutatie-aanroep hoort in deze klasse.
/// </summary>
public static class SportlinkClubClient
{
    /// <summary>Productiewaarde voor <c>HttpClient.BaseAddress</c> — de client bouwt zelf alleen
    /// relatieve request-URI's, zodat een test een <c>HttpClient</c> met een andere
    /// <c>BaseAddress</c> (een lokale fixture-server, zelfde patroon als
    /// <c>FunctionApp.Tests/Sync/SportlinkFixtureServer.cs</c>) kan injecteren zonder een echte
    /// netwerkaanroep.</summary>
    public const string BaseUrl = "https://club.sportlink.com/navajo/entity/common/clubweb/";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Zoekt het <c>PublicMatchId</c> voor een wedstrijd via de #987-reverse-lookup:
    /// <c>MatchProgramOverview</c> met een 1-daags bereik, matchend op <c>ExternalMatchId</c>
    /// (= onze eigen <c>wedstrijdnummer</c>). Retourneert <c>null</c> als de wedstrijd niet in de
    /// respons voorkomt (bv. nog niet bekend bij Sportlink) — geen exception, dit is een normaal
    /// "nog niet gevonden"-resultaat.</summary>
    public static async Task<string?> ResolvePublicMatchIdAsync(
        HttpClient http, long wedstrijdnummer, DateOnly datum, string accessToken, ILogger log)
    {
        var datumStr = datum.ToString("yyyy-MM-dd");
        var entityPath = "competition/match/MatchProgramOverview";
        using var request = NieuwRequest(HttpMethod.Get,
            $"{entityPath}?DateFrom={datumStr}&DateTo={datumStr}", entityPath, accessToken);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        // Respons-vorm niet 100% bevestigd (array direct, of genest onder "Matches") — zie
        // scripts/dev/Invoke-SportlinkMatchProgramLookup.ps1, waar dit al zo behandeld wordt.
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.TryGetProperty("Matches", out var matches) ? matches : default;

        if (items.ValueKind != JsonValueKind.Array)
        {
            log.LogWarning("MatchProgramOverview-respons had onverwachte vorm (geen array, geen Matches-property).");
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("ExternalMatchId", out var extId) &&
                extId.TryGetInt64(out var extIdValue) && extIdValue == wedstrijdnummer &&
                item.TryGetProperty("PublicMatchId", out var publicId))
            {
                return publicId.GetString();
            }
        }

        return null;
    }

    /// <summary>Haalt het volledige, niet-persoonsgebonden wedstrijdobject op. Roept uitsluitend
    /// <c>competition/match/Match</c> aan — niet de officials-/picklist-endpoints (die vallen
    /// buiten de read-only scope van #991).</summary>
    public static async Task<SportlinkMatchInfo?> GetMatchAsync(HttpClient http, string publicMatchId, string accessToken, ILogger log)
    {
        var entityPath = "competition/match/Match";
        using var request = NieuwRequest(HttpMethod.Get,
            $"{entityPath}?PublicMatchId={Uri.EscapeDataString(publicMatchId)}", entityPath, accessToken);

        using var response = await http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<SportlinkMatchInfo>(stream, JsonOpts);
    }

    private static HttpRequestMessage NieuwRequest(HttpMethod method, string relativeUrl, string entityPath, string accessToken)
    {
        // Relatieve URI: combineert met HttpClient.BaseAddress (productie: BaseUrl hierboven,
        // test: een lokale fixture-server) — geen hardcoded absoluut adres in de request zelf.
        var request = new HttpRequestMessage(method, new Uri(relativeUrl, UriKind.Relative));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        // Headers bevestigd verplicht in #990/#987-onderzoek. X-Navajo-Entity is het aangeroepen
        // pad zelf, geen vaste app-naam.
        request.Headers.Add("X-Navajo-Entity", entityPath);
        request.Headers.Add("X-Navajo-Instance", "KNVB");
        request.Headers.Add("X-Navajo-Locale", "nl");
        return request;
    }
}
