using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
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
/// </summary>
public static class SportlinkChangeRequestFunction
{
    private const string RolNaam = "Wedstrijdzaken";

    [Function("SportlinkChangeRequestsGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/change-requests")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkChangeRequestsGet"), "sportlink-wijzigingsverzoeken ophalen",
            async _ =>
            {
                var toggleFout = ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                if (sportlinkClient == null)
                    return new ObjectResult(new { error = "Sportlink-client niet geconfigureerd." }) { StatusCode = 503 };

                var result = await sportlinkClient.GetChangeRequestsAsync(RolNaam);
                var fout = VertaalStatusNaarFout(result.Status);
                if (fout != null) return fout;

                return new OkObjectResult(result.Data ?? new List<SportlinkChangeRequest>());
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

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
                var toggleFout = ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;

                var dto = JsonConvert.DeserializeObject<ChangeRequestActieDto>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                if (string.IsNullOrWhiteSpace(dto?.PublicMatchId))
                    return new BadRequestObjectResult(new { error = "PublicMatchId ontbreekt." });
                if (dto.Actie != "APPROVE" && dto.Actie != "DENY")
                    return new BadRequestObjectResult(new { error = "Actie moet APPROVE of DENY zijn." });
                if (dto.Actie == "DENY" && string.IsNullOrWhiteSpace(dto.Remarks))
                    return new BadRequestObjectResult(new { error = "Toelichting (Remarks) is verplicht bij afwijzen." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                if (sportlinkClient == null)
                    return new ObjectResult(new { error = "Sportlink-client niet geconfigureerd." }) { StatusCode = 503 };

                var auditService = context.InstanceServices.GetService<ISportlinkMutationAuditService>();
                var triggerdDoor = EasyAuthHelper.GetAuditActor(req);
                var auditEntry = new SportlinkMutationAuditEntry(
                    clubCode, RolNaam, triggerdDoor, dto.PublicMatchId, $"MatchChangeRequestAction:{dto.Actie}",
                    WaardeVoor: null,
                    WaardeNa: JsonConvert.SerializeObject(new { publicRequestId, dto.Actie, dto.Remarks }),
                    CorrelationId: publicRequestId);
                var auditId = auditService == null ? (long?)null : await auditService.LogPogingAsync(auditEntry);

                var mutationResult = await sportlinkClient.ActOnChangeRequestAsync(
                    RolNaam, dto.Actie, dto.PublicMatchId, publicRequestId, dto.Remarks);

                var mutationFout = VertaalStatusNaarFout(mutationResult.Status);
                if (mutationFout != null)
                {
                    if (auditId.HasValue) await auditService!.VoltooiAsync(auditId.Value, "Failure", mutationResult.FoutmeldingVoorLog);
                    return mutationFout;
                }

                if (mutationResult.Data == null)
                {
                    if (auditId.HasValue) await auditService!.VoltooiAsync(auditId.Value, "Failure", "Geen respons-data van Sportlink");
                    return new ObjectResult(new { error = "Sportlink gaf geen bruikbare respons." }) { StatusCode = 502 };
                }

                if (auditId.HasValue)
                    await auditService!.VoltooiAsync(auditId.Value, SportlinkMatchFunction.BepaalAuditResultaat(mutationResult.Data),
                        mutationResult.Data.Violations is { Count: > 0 } ? string.Join(", ", mutationResult.Data.Violations) : null);

                return new OkObjectResult(mutationResult.Data);
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

    private sealed class ChangeRequestActieDto
    {
        public string? PublicMatchId { get; set; }
        public string Actie { get; set; } = "";
        public string? Remarks { get; set; }
    }

    private static IActionResult? ControleerToggleEnEgress()
    {
        if (PostgresAppSettings.GetSetting("sportlinkExtensionEnabled") != "1")
            return new ObjectResult(new { error = "Sportlink Web Extension staat uit." }) { StatusCode = 409 };
        if (!EgressGuard.ExternalIntegrationsAllowed())
            return new ObjectResult(new { error = "Uitgaande integraties staan hier niet toe." }) { StatusCode = 503 };
        return null;
    }

    private static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status) => status switch
    {
        SportlinkClubCallStatus.Ok => null,
        SportlinkClubCallStatus.RolNietGekoppeld => new ObjectResult(new
        {
            error = $"Geen Sportlink-koppeling gevonden voor rol '{RolNaam}' — registreer eerst een refresh-token via Instellingen."
        })
        { StatusCode = 409 },
        SportlinkClubCallStatus.HerkoppelingVereist => new ObjectResult(new
        {
            error = $"De Sportlink-koppeling voor rol '{RolNaam}' is verlopen — registreer een nieuw refresh-token via Instellingen."
        })
        { StatusCode = 409 },
        _ => new ObjectResult(new { error = "Sportlink is momenteel niet bereikbaar." }) { StatusCode = 502 },
    };
}
