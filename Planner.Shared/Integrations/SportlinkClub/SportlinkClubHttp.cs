using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Gedeelde, stateloze HTTP-hulpen van de Sportlink-client: endpointconstanten, timeouts, JSON-opties, Navajo-headers en per-aanroep-timeout (#1631).</summary>
internal static class SportlinkClubHttp
{
    /// <summary>Keycloak-tokenendpoint van Sportlink — publiek zodat de tokenregistratie
    /// (<c>SportlinkExtensieRollenFunction</c>) dezelfde constante gebruikt in plaats van een kopie (#1122).</summary>
    internal const string TokenEndpoint = "https://idm.sportlink.com/realms/sportlink/protocol/openid-connect/token";

    internal const string ClientId = "sportlink-club-web";

    // #1387: per-aanroep timeouts, losgekoppeld van HttpClient.Timeout (die staat op de .NET-
    // default van 100s als buitenste veiligheidsnet — zie Program.cs van beide tiers, die géén
    // eigen Timeout meer zetten). Zo krijgt élk endpoint een bewust, gemotiveerd budget in plaats
    // van dat één globale waarde voor alles geldt.
    internal static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// MatchProgramOverview is het enige Sportlink-endpoint dat hier gedocumenteerd 12+ seconden
    /// duurt (zie SportlinkPublicMatchIdWarmupTimerFunction, docs/SPORTLINK-WEB-EXTENSION.md).
    /// Tegen <see cref="DefaultCallTimeout"/> was de marge nagenoeg nul — elke extra vertraging
    /// timede uit en gaf vóór #1387 een valse HTTP 502 (SportlinkEndpointCore.VertaalStatusNaarFout
    /// kon een timeout niet onderscheiden van een echte Sportlink-fout). Ruimere, uitsluitend voor
    /// dit endpoint gemotiveerde marge — geen wijziging van het budget van de overige, snellere
    /// endpoints.
    /// </summary>
    internal static readonly TimeSpan ReverseLookupCallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Wachttijd vóór de ene, begrensde retry bij een transiënte fout (zie
    /// <see cref="IsTransientFout"/>) — los van, en aanvullend op, de bestaande 401-retry in
    /// <see cref="SportlinkClubTransport.ExecuteWithTokenRetryAsync{T}"/>.</summary>
    internal static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(2);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Een fout die de moeite waard is om één keer te herhalen (#1387): een timeout/netwerkfout, of
    /// een 5xx van Sportlink zelf — beide zijn typisch van voorbijgaande aard. Een 4xx (los van de
    /// al apart afgehandelde 401) is een inhoudelijke afwijzing en wordt niet beter van herhalen.
    /// Of er daadwerkelijk herhaald wordt, bepaalt <see cref="RetryBeleid"/> (#1417): alleen voor
    /// idempotente leesaanroepen.
    /// </summary>
    internal static bool IsTransientFout(SportlinkClubCallStatus status, int? httpStatusCode) =>
        status == SportlinkClubCallStatus.NetwerkFout ||
        (status == SportlinkClubCallStatus.SportlinkFout && httpStatusCode is >= 500 and <= 599);

    /// <summary>Eén gedeelde log+wacht-stap vóór de ene, begrensde transiënte retry (#1387) — zowel
    /// <see cref="SportlinkClubTransport.ExecuteWithTokenRetryAsync{T}"/> als het token-refreshpad in
    /// <see cref="SportlinkClubTokenBeheer.RefreshTokenIfNeededAsync"/> gebruiken dezelfde stap, alleen de beschrijving in
    /// het logbericht verschilt.</summary>
    internal static async Task WachtVoorTransienteRetryAsync(ILogger logger, string beschrijving, CancellationToken cancellationToken)
    {
        logger.LogWarning("Transiënte Sportlink-fout ({Beschrijving}) — één retry na {Delay}s",
            beschrijving, TransientRetryDelay.TotalSeconds);
        await Task.Delay(TransientRetryDelay, cancellationToken);
    }

    /// <summary>Kortere vorm voor de meerderheid van de aanroepen, die <see cref="DefaultCallTimeout"/>
    /// gebruiken — alleen de gedocumenteerd trage reverse-lookup geeft expliciet
    /// <see cref="ReverseLookupCallTimeout"/> mee via de overload hieronder.</summary>
    internal static Task<HttpResponseMessage> VerstuurMetTimeoutAsync(
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
    internal static async Task<HttpResponseMessage> VerstuurMetTimeoutAsync(
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
    internal static void ZetSportlinkHeaders(HttpRequestMessage request, string entityName, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Navajo-Entity", entityName);
        request.Headers.Add("X-Navajo-Instance", "KNVB");
        request.Headers.Add("X-Navajo-Locale", "nl");
    }
}
