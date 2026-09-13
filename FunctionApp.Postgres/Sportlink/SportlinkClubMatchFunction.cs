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
/// Oefenwedstrijd ("clubwedstrijd") aanmaken bij Sportlink (#997, epic #986) — scaffolding, geen
/// volledige implementatie. Van alle #986-sub-issues heeft dit issue de MEESTE onbekenden:
/// volledige requestbody onbevestigd, meerdere picklist-vormen onbekend, delete-methode onbekend.
/// <para>
/// <b>Structureel anders dan de andere Sportlink-mutatiefuncties:</b> <see
/// cref="SportlinkMatchFunction"/> en <see cref="SportlinkChangeRequestFunction"/> werken op een AL
/// BESTAANDE wedstrijd (eerst opzoeken, dan pas de guard aanroepen). Hier bestaat er vooraf geen
/// <c>PublicMatchId</c>, geen <c>wedstrijdcode</c>, geen <c>SportlinkMatch</c> om te guarden — dus
/// GEEN <see cref="SportlinkMutationGuard"/>-check. In plaats daarvan gelden alleen ONZE EIGEN
/// regels (de <c>sportlinkExtensionEnabled</c>-toggle + <c>EgressGuard.ExternalIntegrationsAllowed()</c>),
/// zelfde patroon als <see cref="SportlinkChangeRequestFunction"/> (die om een vergelijkbare reden
/// — een verzoek van een tegenstander, geen mutatie op onze eigen wedstrijdgegevens — ook geen
/// <see cref="SportlinkMutationGuard"/>-check heeft).
/// </para>
/// <para>
/// <b>Audit-placeholder:</b> <see cref="SportlinkMutationAuditEntry"/> vereist een verplicht,
/// niet-leeg <c>PublicMatchId</c>-veld — dat bestaat nog niet bij het aanmaken. De Pending-rij
/// gebruikt daarom de placeholder-waarde <c>"NIEUW"</c>, met een gegenereerde GUID in
/// <c>CorrelationId</c> om de Pending- en Voltooid-rij aan elkaar te koppelen (zelfde rij wordt
/// altijd via het teruggegeven <c>auditId</c> voltooid, de GUID is puur ter identificatie in de
/// audit-tabel zelf). Zie de <c>TODO</c> bij <see cref="ISportlinkMutationAuditService.VoltooiAsync"/>
/// hieronder voor waarom het écht opslaan van het teruggekregen <c>PublicMatchId</c> in deze ronde
/// bewust niet is gebouwd.
/// </para>
/// </summary>
public static class SportlinkClubMatchFunction
{
    private const string RolNaam = "Wedstrijdzaken";
    private const string AuditPublicMatchIdPlaceholder = "NIEUW";

    /// <summary>
    /// <c>POST /api/sportlink/club-match</c> — maakt een nieuwe oefenwedstrijd aan. Blijft door de
    /// forceDryRun-code-lock (<c>CreateClubMatchAsync</c>, #997) altijd gesimuleerd totdat een mens
    /// (nooit een agent, zie docs/SPORTLINK-WEB-EXTENSION.md §4.4) de body live heeft bevestigd.
    /// </summary>
    [Function("SportlinkClubMatchPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sportlink/club-match")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkClubMatchPost"), "oefenwedstrijd aanmaken",
            async clubCode =>
            {
                var toggleFout = ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;

                var dto = JsonConvert.DeserializeObject<ClubMatchAanmakenDto>(
                    await new StreamReader(req.Body).ReadToEndAsync());
                if (dto?.MatchDateTime == null)
                    return new BadRequestObjectResult(new { error = "MatchDateTime is verplicht." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                if (sportlinkClient == null)
                    return new ObjectResult(new { error = "Sportlink-client niet geconfigureerd." }) { StatusCode = 503 };

                var aanvraag = new SportlinkClubMatchAanvraag(
                    dto.MatchDateTime.Value,
                    dto.Duration ?? 90,
                    dto.AgeClassCode,
                    dto.Description,
                    dto.PublicHomeTeamId,
                    dto.PublicAwayTeamId,
                    dto.FacilityId,
                    dto.FieldId,
                    dto.ExternalMatchId);

                var auditService = context.InstanceServices.GetService<ISportlinkMutationAuditService>();
                var triggerdDoor = EasyAuthHelper.GetAuditActor(req);
                // Placeholder-aanpak (zie klasse-doc-comment hierboven): PublicMatchId bestaat nog
                // niet, CorrelationId koppelt Pending- en Voltooid-rij.
                var correlationId = Guid.NewGuid().ToString();
                var auditEntry = new SportlinkMutationAuditEntry(
                    clubCode, RolNaam, triggerdDoor, AuditPublicMatchIdPlaceholder, "CreateClubMatch",
                    WaardeVoor: null,
                    WaardeNa: JsonConvert.SerializeObject(dto),
                    CorrelationId: correlationId);
                var auditId = auditService == null ? (long?)null : await auditService.LogPogingAsync(auditEntry);

                var mutationResult = await sportlinkClient.CreateClubMatchAsync(RolNaam, aanvraag);

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
                {
                    // TODO(#997-vervolg): het écht opslaan van mutationResult.Data.PublicMatchId
                    // vereist een uitbreiding van ISportlinkMutationAuditService.VoltooiAsync (een
                    // extra optionele parameter) — raakt beide tiers
                    // (FunctionApp/Sportlink/SqlSportlinkMutationAuditService.cs ook) en is niet
                    // nodig zolang dit pad toch altijd "DryRunLocked" teruggeeft (ClubMatchLiveBevestigd
                    // = false in SportlinkClubClient). Bouw die uitbreiding pas zodra het endpoint
                    // live bevestigd is en dit pad daadwerkelijk een PublicMatchId kan opleveren.
                    await auditService!.VoltooiAsync(
                        auditId.Value,
                        SportlinkMatchFunction.BepaalAuditResultaat(mutationResult.Data),
                        mutationResult.Data.Violations is { Count: > 0 } ? string.Join(", ", mutationResult.Data.Violations) : null);
                }

                return new OkObjectResult(mutationResult.Data);
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

    /// <summary>
    /// <c>GET /api/sportlink/club-match/picklists</c> — de twee ondersteunende picklists die het
    /// formulier nodig heeft (#997, bewust beperkte scope: alleen Teams + Location, niet de overige
    /// drie uit het issue). Read-only en persoonsgegevensvrij — bewust NIET automatisch geladen bij
    /// het openen van de pagina, alleen op expliciet verzoek vanuit de UI (zie BlazorAdmin).
    /// </summary>
    [Function("SportlinkClubMatchPickListsGet")]
    public static Task<IActionResult> GetPickLists(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/club-match/picklists")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkClubMatchPickListsGet"), "oefenwedstrijd-picklists ophalen",
            async _ =>
            {
                var toggleFout = ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                if (sportlinkClient == null)
                    return new ObjectResult(new { error = "Sportlink-client niet geconfigureerd." }) { StatusCode = 503 };

                var result = await sportlinkClient.GetClubMatchPickListsAsync(RolNaam);
                var fout = VertaalStatusNaarFout(result.Status);
                if (fout != null) return fout;

                return new OkObjectResult(result.Data ?? new SportlinkClubMatchPickLists(
                    Array.Empty<SportlinkPickListItem>(), Array.Empty<SportlinkPickListItem>()));
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

    private sealed class ClubMatchAanmakenDto
    {
        public DateTime? MatchDateTime { get; set; }
        public int? Duration { get; set; }
        public string? AgeClassCode { get; set; }
        public string? Description { get; set; }
        public string? PublicHomeTeamId { get; set; }
        public string? PublicAwayTeamId { get; set; }
        public string? FacilityId { get; set; }
        public string? FieldId { get; set; }
        public long? ExternalMatchId { get; set; }
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
