using Microsoft.Extensions.Logging;

namespace Planner.Shared.Integrations.SportlinkClub;

/// <summary>
/// HTTP client voor Sportlink Club API (façade). Sinds #1631 een dunne compositieklasse: elke
/// aanroep delegeert naar een domeinklasse (<see cref="SportlinkWedstrijdLezer"/>,
/// <see cref="SportlinkClubFormulierLezer"/>, <see cref="SportlinkWedstrijdDetails"/>,
/// <see cref="SportlinkMutaties"/>) die uitsluitend via <see cref="SportlinkClubTransport"/> met
/// Sportlink praat (token-refresh, eenmalige 401-retry, retrybeleid, Navajo-headers). Er is dus
/// nog altijd precies één route naar Sportlink. De contracten staan op
/// <see cref="ISportlinkClubClient"/>; guardrails/audit horen bij de aanroeper, zie
/// <see cref="SportlinkMutationGuard"/>.
/// </summary>
public class SportlinkClubClient : ISportlinkClubClient
{
    /// <summary>Keycloak-tokenendpoint van Sportlink — publiek zodat de tokenregistratie
    /// (<c>SportlinkExtensieRollenFunction</c>) dezelfde constante gebruikt in plaats van een kopie (#1122).</summary>
    public const string TokenEndpoint = SportlinkClubHttp.TokenEndpoint;
    public const string ClientId = SportlinkClubHttp.ClientId;

    private readonly SportlinkClubTransport _transport;
    private readonly SportlinkWedstrijdLezer _lezer;
    private readonly SportlinkClubFormulierLezer _formulier;
    private readonly SportlinkWedstrijdDetails _details;
    private readonly SportlinkMutaties _mutaties;

    /// <param name="httpClient">Onderliggende HTTP-client.</param>
    /// <param name="tokenStore">Rol-gescoped refresh-tokenopslag.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="isDryRun">
    /// #998: delegate (geen settings/DB-afhankelijkheid in <c>Planner.Shared</c>, zelfde
    /// ontkoppelingspatroon als <see cref="ISportlinkClubTokenStore"/>) die bij elke mutatie-
    /// aanroep opnieuw wordt gelezen — niet één keer bij opstarten. <c>true</c> betekent: token-
    /// refresh, snapshot-GETs en de UserInfo-GET lopen echt, maar de daadwerkelijke PUT/POST naar
    /// Sportlink wordt overgeslagen (zie <see cref="SportlinkMutatieUitvoerder"/>). Standaard
    /// <c>() =&gt; false</c> als niet meegegeven — backwards compatible met bestaande tests/callers.
    /// </param>
    public SportlinkClubClient(
        HttpClient httpClient,
        ISportlinkClubTokenStore tokenStore,
        ILogger<SportlinkClubClient> logger,
        Func<bool>? isDryRun = null,
        SportlinkAutoLoginCoordinator? autoLogin = null)
    {
        _transport = new SportlinkClubTransport(httpClient, tokenStore, logger, isDryRun ?? (() => false), autoLogin);
        var uitvoerder = new SportlinkMutatieUitvoerder(_transport);
        _lezer = new SportlinkWedstrijdLezer(_transport);
        _formulier = new SportlinkClubFormulierLezer(_transport);
        _details = new SportlinkWedstrijdDetails(_transport, uitvoerder);
        _mutaties = new SportlinkMutaties(_transport, uitvoerder);
    }

    public Task<SportlinkClubResponse<SportlinkMatch>> GetMatchAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
        => _lezer.GetMatchAsync(functioneleRol, publicMatchId, cancellationToken);

    public Task<SportlinkClubResponse<string>> GetMatchRawJsonAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
        => _lezer.GetMatchRawJsonAsync(functioneleRol, publicMatchId, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMatchProgramEntry>> ResolvePublicMatchIdAsync(
        string functioneleRol,
        long wedstrijdnummer,
        DateOnly datum,
        CancellationToken cancellationToken = default)
        => _lezer.ResolvePublicMatchIdAsync(functioneleRol, wedstrijdnummer, datum, cancellationToken);

    public Task<SportlinkClubResponse<IReadOnlyList<SportlinkMatchProgramEntry>>> GetMatchProgramOverviewAsync(
        string functioneleRol,
        DateOnly datum,
        CancellationToken cancellationToken = default)
        => _lezer.GetMatchProgramOverviewAsync(functioneleRol, datum, cancellationToken);

    public Task<SportlinkClubCallStatus> VerversTokenAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default)
        => _transport.VerversTokenAsync(functioneleRol, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateDressingRoomsAsync(
        string functioneleRol,
        string publicMatchId,
        string? homeDressingRoomId,
        string? awayDressingRoomId,
        string? officialDressingRoomId,
        CancellationToken cancellationToken = default)
        => _details.UpdateDressingRoomsAsync(functioneleRol, publicMatchId, homeDressingRoomId, awayDressingRoomId, officialDressingRoomId, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMutationResult>> UpdateFieldAsync(
        string functioneleRol,
        string publicMatchId,
        string? fieldId,
        string? fieldSize,
        int? fieldOffset,
        bool isForceUpdate,
        CancellationToken cancellationToken = default)
        => _details.UpdateFieldAsync(functioneleRol, publicMatchId, fieldId, fieldSize, fieldOffset, isForceUpdate, cancellationToken);

    public Task<SportlinkClubResponse<IReadOnlyList<SportlinkChangeRequest>>> GetChangeRequestsAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default)
        => _lezer.GetChangeRequestsAsync(functioneleRol, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMutationResult>> ActOnChangeRequestAsync(
        string functioneleRol,
        string actie,
        string publicMatchId,
        string publicRequestId,
        string? remarks,
        CancellationToken cancellationToken = default)
        => _mutaties.ActOnChangeRequestAsync(functioneleRol, actie, publicMatchId, publicRequestId, remarks, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMutationResult>> AssignOfficialsAsync(
        string functioneleRol,
        string publicMatchId,
        IReadOnlyList<SportlinkOfficialToewijzing> officials,
        CancellationToken cancellationToken = default)
        => _mutaties.AssignOfficialsAsync(functioneleRol, publicMatchId, officials, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMatchChangeRequestResult>> RequestMatchChangeAsync(
        string functioneleRol,
        string publicMatchId,
        DateOnly? nieuweDatum,
        TimeOnly? nieuweStartTijd,
        string? nieuweFacilityId,
        string toelichting,
        CancellationToken cancellationToken = default)
        => _details.RequestMatchChangeAsync(functioneleRol, publicMatchId, nieuweDatum, nieuweStartTijd, nieuweFacilityId, toelichting, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMutationResult>> CreateClubMatchAsync(
        string functioneleRol,
        SportlinkClubMatchAanvraag aanvraag,
        CancellationToken cancellationToken = default)
        => _mutaties.CreateClubMatchAsync(functioneleRol, aanvraag, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkMutationResult>> DeleteClubMatchAsync(
        string functioneleRol,
        string publicMatchId,
        CancellationToken cancellationToken = default)
        => _mutaties.DeleteClubMatchAsync(functioneleRol, publicMatchId, cancellationToken);

    public Task<SportlinkClubResponse<IReadOnlyList<SportlinkVeldplannerBlok>>> GetVeldplannerAsync(
        string functioneleRol, string facilityId, DateOnly datum, CancellationToken cancellationToken = default)
        => _lezer.GetVeldplannerAsync(functioneleRol, facilityId, datum, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkClubMatchPickLists>> GetClubMatchPickListsAsync(
        string functioneleRol,
        CancellationToken cancellationToken = default)
        => _formulier.GetClubMatchPickListsAsync(functioneleRol, cancellationToken);

    public Task<SportlinkClubResponse<SportlinkClubMatchContext>> GetClubMatchContextAsync(
        string functioneleRol, CancellationToken cancellationToken = default)
        => _formulier.GetClubMatchContextAsync(functioneleRol, cancellationToken);

    /// <summary>Zie <see cref="SportlinkClubTokenBeheer.ValideerRefreshTokenAsync"/>.</summary>
    public static Task<bool> ValideerRefreshTokenAsync(HttpClient http, string refreshToken, CancellationToken cancellationToken = default)
        => SportlinkClubTokenBeheer.ValideerRefreshTokenAsync(http, refreshToken, cancellationToken);
}
