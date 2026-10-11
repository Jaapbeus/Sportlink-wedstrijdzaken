using Microsoft.Extensions.Logging;
using System.Text.Json;
using static Planner.Shared.Integrations.SportlinkClub.SportlinkClubHttp;
using static Planner.Shared.Integrations.SportlinkClub.SportlinkMutatieBodies;
using static Planner.Shared.Integrations.SportlinkClub.SportlinkMutatieUitvoerder;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>Mutaties op wijzigingsverzoeken, officials en clubwedstrijden (#1631, verplaatst uit het vroegere SportlinkClubClient.Mutaties-bestand).</summary>
internal sealed class SportlinkMutaties
{
    private readonly SportlinkClubTransport _transport;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly SportlinkMutatieUitvoerder _uitvoerder;

    internal SportlinkMutaties(SportlinkClubTransport transport, SportlinkMutatieUitvoerder uitvoerder)
    {
        _transport = transport;
        _httpClient = transport.Http;
        _logger = transport.Logger;
        _uitvoerder = uitvoerder;
    }

    private const string MatchChangeRequestActionEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/changerequest/MatchChangeRequestAction";

    private const string UpdateMatchOfficialsEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/official/MatchOfficialsAction";

    private const string UserInfoEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/user/UserInfo";

    // #997: aanmaken van een nieuwe oefenwedstrijd ("clubwedstrijd") — zie CreateClubMatchAsync.
    private const string ClubMatchEndpoint = "https://club.sportlink.com/navajo/entity/common/clubweb/competition/match/clubmatch/ClubMatch";

    // #994: dit endpoint/deze body-vorm is NOOIT live bevestigd (reverse-engineered, geen
    // netwerktrace) — daarom staat de mutatie hard op forceDryRun totdat een mens (nooit een
    // agent, zie docs/SPORTLINK-WEB-EXTENSION.md §4.4) een live trace heeft gedaan en deze
    // constante in een aparte, reviewbare PR op true zet. Grep-baar bij naam.
    // Eigenaar: op true gezet op 27-09-2026 na live-bevestiging buiten agent-sessie om (#1319).
    private const bool MatchOfficialsActionLiveBevestigd = true;

    // #997: idem voor het aanmaken van een oefenwedstrijd — dit issue heeft van alle #986-sub-
    // issues de MEESTE onbekenden (volledige body onbevestigd, meerdere picklist-vormen onbekend,
    // delete-methode onbekend). Grep-baar bij naam, zelfde patroon als MatchOfficialsActionLiveBevestigd.
    // Eigenaar: op true gezet op 27-09-2026 na live-bevestiging buiten agent-sessie om (#1319).
    private const bool ClubMatchLiveBevestigd = true;

    // #1440/#1458: verwijderen van een clubwedstrijd. Methode (DELETE), parameter (PublicMatchId als
    // querystring, geen body) en foutvorm (HTTP 420 met Violations) kwamen uit Sportlinks publieke
    // frontend-bundle; de succesrespons is live vastgesteld (03-10-2026): een JSON-object
    // { "PublicMatchId": ..., "IsSuccess": true }, niet leeg. Door de eigenaar op true gezet
    // (besluit 03-10-2026, #1458): de aanroep volgt vanaf nu de club-instelling sportlinkDryRun.
    // Grep-baar bij naam; SportlinkClubMatchDeleteTests bewaakt hem.
    private const bool ClubMatchDeleteLiveBevestigd = true;

    public Task<SportlinkClubResponse<SportlinkMutationResult>> ActOnChangeRequestAsync(
        string functioneleRol,
        string actie,
        string publicMatchId,
        string publicRequestId,
        string? remarks,
        CancellationToken cancellationToken = default)
        => _transport.ExecuteWithTokenRetryAsync(
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
        return _uitvoerder.PutMutationAsync(
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
        => _transport.ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            (token, ct) => PutMatchOfficialsAsync(publicMatchId, officials, token, ct),
            cancellationToken);

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PutMatchOfficialsAsync(
        string publicMatchId, IReadOnlyList<SportlinkOfficialToewijzing> officials, string token, CancellationToken cancellationToken)
    {
        return _uitvoerder.PutMutationAsync(
            UpdateMatchOfficialsEndpoint,
            "competition/match/official/MatchOfficialsAction",
            BuildMatchOfficialsBody(publicMatchId, officials),
            token,
            cancellationToken,
            new MutatieOpties(ForceDryRun: !MatchOfficialsActionLiveBevestigd, VerrijkResultaat: VerrijkOfficialsResultaat));
    }

    private sealed record SportlinkUserInfo(string? PublicPersonId);

    /// <summary>
    /// Maakt een nieuwe oefenwedstrijd aan (#997) — zie <see cref="ISportlinkClubClient.CreateClubMatchAsync"/>.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkMutationResult>> CreateClubMatchAsync(
        string functioneleRol,
        SportlinkClubMatchAanvraag aanvraag,
        CancellationToken cancellationToken = default)
        => _transport.ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            (token, ct) => PostClubMatchAsync(aanvraag, token, ct),
            cancellationToken);

    private Task<SportlinkClubResponse<SportlinkMutationResult>> PostClubMatchAsync(
        SportlinkClubMatchAanvraag aanvraag, string token, CancellationToken cancellationToken)
    {
        return _uitvoerder.PutMutationAsync(
            ClubMatchEndpoint,
            "competition/match/clubmatch/ClubMatch",
            BuildClubMatchBody(aanvraag),
            token,
            cancellationToken,
            new MutatieOpties(ForceDryRun: !ClubMatchLiveBevestigd, Method: HttpMethod.Post));
    }

    /// <summary>
    /// Verwijdert een clubwedstrijd (#1440) — zie <see cref="ISportlinkClubClient.DeleteClubMatchAsync"/>.
    /// Sinds #1458 live (<see cref="ClubMatchDeleteLiveBevestigd"/> = <c>true</c>): de aanroep volgt de
    /// club-instelling <c>sportlinkDryRun</c>; bij dry-run verlaat er geen DELETE deze client.
    /// </summary>
    public Task<SportlinkClubResponse<SportlinkMutationResult>> DeleteClubMatchAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
        => _transport.ExecuteWithTokenRetryAsync(
            functioneleRol,
            RetryBeleid.Mutatie,
            (token, ct) => _uitvoerder.PutMutationAsync(
                BouwClubMatchDeleteUrl(publicMatchId),
                "competition/match/clubmatch/ClubMatchDelete",
                body: null,
                token,
                ct,
                new MutatieOpties(
                    ForceDryRun: !ClubMatchDeleteLiveBevestigd,
                    Method: HttpMethod.Delete,
                    LegeSuccesBodyIsSucces: true)),
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
}
