using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Gedeelde uitvoering en responsinterpretatie van mutaties (PutMutationAsync) (#1493) — zelfde klasse, alleen verplaatst. Geen gedragswijziging.</summary>
public partial class SportlinkClubClient
{
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
        MutatieOpties? opties = null)
    {
        var o = opties ?? new MutatieOpties();
        try
        {
            // Direct na serialisatie (zodat een serialisatiefout alsnog opduikt) en vóór het
            // versturen: dry-run slaat uitsluitend de daadwerkelijke PUT over. Token-refresh en de
            // voorbereidende GETs (snapshot, UserInfo) hebben al plaatsgevonden vóórdat deze methode
            // werd aangeroepen — dat maakt een dry-run realistisch (#998).
            // #1440: body == null (DELETE) → geen content; de parameters staan dan in de URL.
            var serializedBody = body == null ? null : JsonSerializer.Serialize(body);
            // #994: ForceDryRun is een code-niveau lock voor een nog-onbevestigde mutatie —
            // ONAFHANKELIJK van _isDryRun() (de club-instelling sportlinkDryRun).
            if (o.ForceDryRun || _isDryRun())
                return DryRunRespons(endpoint, entityName, o.ForceDryRun);

            var request = BouwMutatieRequest(endpoint, entityName, serializedBody, token, o.Method ?? HttpMethod.Put);
            var response = await VerstuurMetTimeoutAsync(ct => _httpClient.SendAsync(request, ct), cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new SportlinkClubResponse<SportlinkMutationResult>(
                    SportlinkClubCallStatus.SportlinkFout, null, $"Unauthorized bij {entityName} endpoint", 401);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return InterpreteerMutatieRespons(entityName, response.StatusCode, json, o);
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

    /// <summary>Opties van één mutatieaanroep (#1493, vervangt de losse vlagparameters).</summary>
    /// <param name="ForceDryRun">Code-niveau lock: altijd simuleren, ongeacht de club-instelling.</param>
    /// <param name="VerrijkResultaat">Optionele nabewerking op de ruwe JSON + het basisresultaat.</param>
    /// <param name="Method">Standaard <c>PUT</c>; POST (#997) of DELETE (#1440).</param>
    /// <param name="LegeSuccesBodyIsSucces">Een 2xx zonder body telt als succes (DELETE, #1440).</param>
    private sealed record MutatieOpties(
        bool ForceDryRun = false,
        Func<string, SportlinkMutationResult, SportlinkMutationResult>? VerrijkResultaat = null,
        HttpMethod? Method = null,
        bool LegeSuccesBodyIsSucces = false);

    private static HttpRequestMessage BouwMutatieRequest(
        string endpoint, string entityName, string? serializedBody, string token, HttpMethod httpMethod)
    {
        var request = new HttpRequestMessage(httpMethod, endpoint);
        if (serializedBody != null)
            request.Content = new StringContent(serializedBody, System.Text.Encoding.UTF8, "application/json");
        ZetSportlinkHeaders(request, entityName, token);
        return request;
    }

    private SportlinkClubResponse<SportlinkMutationResult> DryRunRespons(string endpoint, string entityName, bool forceDryRun)
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

    /// <summary>Vertaalt een (niet-401) Sportlink-mutatierespons naar een <see cref="SportlinkMutationResult"/>.</summary>
    private SportlinkClubResponse<SportlinkMutationResult> InterpreteerMutatieRespons(
        string entityName, System.Net.HttpStatusCode statusCode, string json, MutatieOpties opties)
    {
        var status = (int)statusCode;
        if (opties.LegeSuccesBodyIsSucces && IsLegeSuccesRespons(statusCode, json))
            return new SportlinkClubResponse<SportlinkMutationResult>(
                SportlinkClubCallStatus.Ok, new SportlinkMutationResult(true, null), null, status);

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
                SportlinkClubCallStatus.SportlinkFout, null, "JSON deserialisatie fout", status);
        }

        if (raw == null)
        {
            _logger.LogWarning("{Entity} endpoint gaf {StatusCode} met lege/onherkenbare respons", entityName, statusCode);
            return new SportlinkClubResponse<SportlinkMutationResult>(
                SportlinkClubCallStatus.SportlinkFout, null, $"{entityName} endpoint gaf {statusCode} zonder herkenbare respons", status);
        }

        // #1417: een 5xx is een serverfout van Sportlink, geen inhoudelijke afwijzing — ook als
        // de body toevallig als JSON parseert. Uniform: altijd SportlinkFout mét statuscode, zodat
        // SportlinkEndpointCore.VertaalStatusNaarFout één pad kent.
        if (status is >= 500 and <= 599)
        {
            _logger.LogWarning("{Entity} endpoint gaf serverfout {StatusCode}", entityName, statusCode);
            return new SportlinkClubResponse<SportlinkMutationResult>(
                SportlinkClubCallStatus.SportlinkFout, null, $"{entityName} endpoint gaf serverfout {status}", status);
        }

        var violations = raw.Violations is { Count: > 0 }
            ? raw.Violations.Select(kv => $"{kv.Key}: {kv.Value}").ToList()
            : raw.ViolationCodes;
        var isSuccess = raw.Error != true && status is >= 200 and <= 299;
        // #1427: een afwijzing zonder Violations (live gezien: HTTP 602 op ClubMatch) toonde in
        // de GUI alleen "afgewezen". Sportlinks eigen Message geeft dan de reden — die gaat
        // naar de gebruiker; in de log alleen de statuscode, nooit de body.
        if (!isSuccess && violations is not { Count: > 0 })
        {
            _logger.LogWarning("{Entity} endpoint wees af met {StatusCode} zonder violations", entityName, status);
            violations = new List<string> { $"Sportlink {status}: {raw.Message ?? "geen foutmelding"}" };
        }
        // #997: PublicMatchId komt alleen terug op de ClubMatch-aanmaak-respons; voor elke andere
        // mutatie-respons blijft raw.PublicMatchId null (bestaand gedrag ongewijzigd).
        var mutationResult = new SportlinkMutationResult(isSuccess, violations, PublicMatchId: raw.PublicMatchId);
        if (opties.VerrijkResultaat != null)
            mutationResult = opties.VerrijkResultaat(json, mutationResult);
        return new SportlinkClubResponse<SportlinkMutationResult>(
            SportlinkClubCallStatus.Ok, mutationResult, null, status);
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
}
