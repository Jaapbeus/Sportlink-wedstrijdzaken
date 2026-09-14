using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Integrations.SportlinkClub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Inkomende wijzigingsverzoeken van tegenstanders (#996, epic #986). Niet wedstrijdcode-gescoped
/// (geen eigen route-segment daarvoor) — Sportlinks <c>MatchChangeRequests</c>-endpoint levert alle
/// openstaande verzoeken voor het gekoppelde serviceaccount in één keer.
/// <para>
/// <b>Geen <see cref="SportlinkMutationGuard"/>-check hier:</b> die guard bewaakt of ONZE eigen
/// wedstrijd een bepaalde mutatie toestaat (<c>IsEditFieldAllowed</c> e.d.) — het goedkeuren/
/// afwijzen van een verzoek van een TEGENSTANDER is geen mutatie op onze eigen wedstrijdgegevens en
/// heeft dus geen equivalente vlag in <see cref="SportlinkMatch"/>. Audit-logging blijft wel
/// verplicht (zelfde reden als #992/#993: Sportlink's eigen log toont alleen de servicenaam, niet
/// de individuele webapp-gebruiker).
/// </para>
/// <para>
/// <b>Sinds #1111 verrijkt de GET elk verzoek met onze eigen wedstrijdcontext</b>
/// (<see cref="SportlinkWedstrijdContext"/>) via de PublicMatchId-cache → <c>his.matches</c>. Bewust
/// niet via extra Sportlink-velden: die zijn nooit live bevestigd. Faalt de verrijking (database
/// weg, tabel ontbreekt), dan komt de lijst zonder context terug — de verzoeken zelf mogen daar
/// nooit door verdwijnen.
/// </para>
/// </summary>
public static class SportlinkChangeRequestFunction
{
    private const string RolNaam = SportlinkEndpointSupport.RolWedstrijdzaken;

    [Function("SportlinkChangeRequestsGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/change-requests")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("SportlinkChangeRequestsGet");
        return AdminEndpoint.ExecuteAsync(req, log, "sportlink-wijzigingsverzoeken ophalen",
            async clubCode =>
            {
                var toggleFout = SportlinkEndpointSupport.ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;
                var (sportlinkClient, clientFout) = SportlinkEndpointSupport.ClientOfFout(context);
                if (clientFout != null) return clientFout;

                var result = await sportlinkClient.GetChangeRequestsAsync(RolNaam);
                var fout = VertaalStatusNaarFout(result.Status);
                if (fout != null) return fout;

                var verzoeken = result.Data ?? new List<SportlinkChangeRequest>();
                var wedstrijdContext = await ZoekWedstrijdContextAsync(verzoeken, clubCode, log);
                return new OkObjectResult(SportlinkChangeRequestOverzichtItem.Verrijk(verzoeken, wedstrijdContext));
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);
    }

    /// <summary>#1111: één query voor alle PublicMatchIds; elke fout hier is een waarschuwing, geen 500.</summary>
    private static async Task<IReadOnlyDictionary<string, SportlinkWedstrijdContext>> ZoekWedstrijdContextAsync(
        IReadOnlyCollection<SportlinkChangeRequest> verzoeken, string clubCode, ILogger log)
    {
        var ids = verzoeken
            .Select(v => v.PublicMatchId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return new Dictionary<string, SportlinkWedstrijdContext>();

        try
        {
            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();
            return await SportlinkPublicMatchIdRepository.ZoekWedstrijdenBijPublicMatchIdsAsync(connection, ids, clubCode);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Wedstrijdcontext voor {Aantal} wijzigingsverzoek(en) kon niet worden opgehaald — lijst zonder context geleverd", ids.Count);
            return new Dictionary<string, SportlinkWedstrijdContext>();
        }
    }

    /// <summary>
    /// <c>PUT /api/sportlink/change-requests/{publicRequestId}/action</c> — goedkeuren of afwijzen.
    /// <paramref name="publicRequestId"/> komt uit de GET hierboven, dus geen eigen wedstrijdcode-
    /// resolutie nodig; <c>PublicMatchId</c> zit al in de request-body (client kent 'm uit dezelfde
    /// GET-respons).
    /// </summary>
    [Function("SportlinkChangeRequestActionPut")]
    public static Task<IActionResult> PutAction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sportlink/change-requests/{publicRequestId}/action")] HttpRequest req,
        string publicRequestId,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkChangeRequestActionPut"), "sportlink-wijzigingsverzoek afhandelen",
            async clubCode =>
            {
                var toggleFout = SportlinkEndpointSupport.ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;

                var dto = await SportlinkEndpointSupport.LeesBodyAsync<ChangeRequestActieDto>(req);
                if (string.IsNullOrWhiteSpace(dto?.PublicMatchId))
                    return new BadRequestObjectResult(new { error = "PublicMatchId ontbreekt." });
                if (dto.Actie != "APPROVE" && dto.Actie != "DENY")
                    return new BadRequestObjectResult(new { error = "Actie moet APPROVE of DENY zijn." });
                if (dto.Actie == "DENY" && string.IsNullOrWhiteSpace(dto.Remarks))
                    return new BadRequestObjectResult(new { error = "Toelichting (Remarks) is verplicht bij afwijzen." });

                var (sportlinkClient, clientFout) = SportlinkEndpointSupport.ClientOfFout(context);
                if (clientFout != null) return clientFout;

                var auditService = context.InstanceServices.GetService<ISportlinkMutationAuditService>();
                var triggerdDoor = EasyAuthHelper.GetAuditActor(req);
                var auditEntry = new SportlinkMutationAuditEntry(
                    clubCode, RolNaam, triggerdDoor, dto.PublicMatchId, $"MatchChangeRequestAction:{dto.Actie}",
                    WaardeVoor: null,
                    WaardeNa: JsonConvert.SerializeObject(new { publicRequestId, dto.Actie, dto.Remarks }),
                    CorrelationId: publicRequestId);
                var auditId = auditService == null ? (long?)null : await auditService.LogPogingAsync(auditEntry);

                var mutationResult = await sportlinkClient!.ActOnChangeRequestAsync(
                    RolNaam, dto.Actie, dto.PublicMatchId, publicRequestId, dto.Remarks);
                return await SportlinkEndpointSupport.RondMutatieAfAsync(
                    mutationResult, auditService, auditId, r => r, data => new OkObjectResult(data));
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

    private sealed class ChangeRequestActieDto
    {
        public string? PublicMatchId { get; set; }
        public string Actie { get; set; } = "";
        public string? Remarks { get; set; }
    }

    private static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status)
        => SportlinkEndpointSupport.VertaalStatusNaarFout(status);
}
