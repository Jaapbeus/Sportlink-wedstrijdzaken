using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.Integrations.SportlinkClub;
using static Planner.Endpoints.Sportlink.ClubMatchEndpointCore;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// Oefenwedstrijd ("clubwedstrijd") aanmaken bij Sportlink (#997, epic #986; formulier herzien in
/// #1116) — scaffolding, geen volledige implementatie. Van alle #986-sub-issues heeft dit issue de
/// MEESTE onbekenden: volledige requestbody onbevestigd, picklist-vormen onbekend, delete-methode
/// onbekend.
/// <para>
/// <b>Sinds #1116 doet de server de vertaling, niet de gebruiker.</b> Het formulier levert alleen
/// wat een wedstrijdsecretaris snel kan invullen: datum, tijd, duur, eigen team (naam uit onze
/// eigen database), tegenstander (vrije tekst) en veld. Deze functie vertaalt dat naar de
/// Sportlink-velden: teamnaam → <c>PublicHomeTeamId</c> + <c>AgeClassCode</c> via
/// <see cref="SportlinkClubMatchRepository"/>, de accommodatie uit de club-instelling
/// <c>accommodatie</c> → <c>FacilityId</c> via de (read-only, echt aangeroepen) Sportlink-
/// locatiepicklist. Het team-ID komt uit de gevalideerde aliassen (<c>public.teamaliassen</c> →
/// <c>his.teams.teamcode</c>), nooit uit eigen naamlogica. Elke vertaling die niet lukt wordt een <i>waarschuwing</i> in de respons, geen
/// fout — het pad is toch code-gelockt en de beheerder moet kunnen zien wat er (gesimuleerd) mee
/// zou gaan.
/// </para>
/// <para>
/// <b>Structureel anders dan de andere Sportlink-mutatiefuncties:</b> <see
/// cref="SportlinkMatchFunction"/> en <see cref="SportlinkChangeRequestFunction"/> werken op een AL
/// BESTAANDE wedstrijd (eerst opzoeken, dan pas de guard aanroepen). Hier bestaat er vooraf geen
/// <c>PublicMatchId</c>, geen <c>wedstrijdcode</c>, geen <c>SportlinkMatch</c> om te guarden — dus
/// GEEN <see cref="SportlinkMutationGuard"/>-check. In plaats daarvan gelden alleen ONZE EIGEN
/// regels (de <c>sportlinkExtensionEnabled</c>-toggle + <c>EgressGuard.ExternalIntegrationsAllowed()</c>),
/// zelfde patroon als <see cref="SportlinkChangeRequestFunction"/>.
/// </para>
/// <para>
/// <b>Audit-placeholder:</b> <see cref="SportlinkMutationAuditEntry"/> vereist een verplicht,
/// niet-leeg <c>PublicMatchId</c>-veld — dat bestaat nog niet bij het aanmaken. De Pending-rij
/// gebruikt daarom de placeholder-waarde <c>"NIEUW"</c>, met een gegenereerde GUID in
/// <c>CorrelationId</c> om de Pending- en Voltooid-rij aan elkaar te koppelen. Zie de <c>TODO</c>
/// bij <see cref="ISportlinkMutationAuditService.VoltooiAsync"/> hieronder voor waarom het écht
/// opslaan van het teruggekregen <c>PublicMatchId</c> bewust niet is gebouwd.
/// </para>
/// </summary>
public static class SportlinkClubMatchFunction
{
    private const string RolNaam = SportlinkEndpointSupport.RolWedstrijdzaken;
    private const string AuditPublicMatchIdPlaceholder = "NIEUW";

    /// <summary>
    /// <c>POST /api/sportlink/club-match</c> — maakt een nieuwe oefenwedstrijd aan. Live bevestigd door de
    /// eigenaar (#1319): volgt de dry-run-instelling van de club (zie docs/SPORTLINK-WEB-EXTENSION.md §4.4).
    /// </summary>
    [Function("SportlinkClubMatchPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sportlink/club-match")] HttpRequest req,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SportlinkClubMatchPost"), "oefenwedstrijd aanmaken",
            async clubCode =>
            {
                var log = context.GetLogger("SportlinkClubMatchPost");

                var toggleFout = SportlinkEndpointSupport.ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;

                var dto = await SportlinkEndpointSupport.LeesBodyAsync<OefenwedstrijdAanmakenDto>(req);
                var validatieFout = Valideer(dto);
                if (validatieFout != null) return validatieFout;

                var (sportlinkClient, clientFout) = SportlinkEndpointSupport.ClientOfFout(context);
                if (clientFout != null) return clientFout;

                var cs = PostgresDatabaseConfig.ConnectionString;
                var team = await SportlinkClubMatchRepository.GetTeamKoppelingAsync(clubCode, dto!.TeamNaam!, cs)
                    ?? VrijeTekstKoppeling(dto);
                if (team == null)
                    return new BadRequestObjectResult(new { error = $"Team '{dto.TeamNaam}' is niet bekend als actief clubteam." });

                string? veldNaam = null;
                if (dto.VeldNummer.HasValue)
                {
                    veldNaam = await SportlinkClubMatchRepository.GetActiefVeldNaamAsync(clubCode, dto.VeldNummer.Value, cs);
                    if (veldNaam == null)
                        return new BadRequestObjectResult(new { error = $"Veld {dto.VeldNummer} is niet bekend als actief veld." });
                }

                var waarschuwingen = new List<string>();
                if (team.SportlinkTeamId == null)
                    waarschuwingen.Add(team.AantalKandidaatIds > 1
                        ? $"Meerdere Sportlink-team-ID's ({team.AantalKandidaatIds}) gekoppeld aan '{team.TeamNaam}' — PublicHomeTeamId blijft leeg totdat de aliassen zijn opgeschoond."
                        : $"Geen Sportlink-team-ID bekend voor '{team.TeamNaam}' (geen KNVB-teamrij als gevalideerde alias gekoppeld) — PublicHomeTeamId blijft leeg.");
                if (string.IsNullOrWhiteSpace(team.Leeftijdscategorie))
                    waarschuwingen.Add($"Geen leeftijdscategorie bekend voor '{team.TeamNaam}' — AgeClassCode blijft leeg.");

                var accommodatie = PostgresAppSettings.GetSetting("accommodatie");
                var facilityId = await BepaalFacilityIdAsync(sportlinkClient!, RolNaam, clubCode, accommodatie, waarschuwingen, log);

                var omschrijving = BouwOmschrijving(dto.Description, team.TeamNaam, dto.Tegenstander!, veldNaam);
                var aanvraag = new SportlinkClubMatchAanvraag(
                    dto.MatchDateTime!.Value,
                    dto.Duration ?? 90,
                    AgeClassCode: team.Leeftijdscategorie,
                    Description: omschrijving,
                    PublicHomeTeamId: team.SportlinkTeamId?.ToString(),
                    PublicAwayTeamId: dto.Tegenstander!.Trim(),
                    FacilityId: facilityId,
                    // Veld-ID: gaat volgens het plan van #997 (stap 4) pas ná aanmaken via het
                    // bestaande veld-mutatiepad (#993) op de nieuwe PublicMatchId. Het gekozen veld
                    // reist nu mee in de omschrijving en in de audit.
                    FieldId: null);

                var auditService = context.InstanceServices.GetService<ISportlinkMutationAuditService>();
                var triggerdDoor = EasyAuthHelper.GetAuditActor(req);
                var correlationId = Guid.NewGuid().ToString();
                var auditEntry = new SportlinkMutationAuditEntry(
                    clubCode, RolNaam, triggerdDoor, AuditPublicMatchIdPlaceholder, "CreateClubMatch",
                    WaardeVoor: null,
                    WaardeNa: JsonConvert.SerializeObject(new { Invoer = dto, Aanvraag = aanvraag, VeldNaam = veldNaam, Waarschuwingen = waarschuwingen }),
                    CorrelationId: correlationId);
                var auditId = auditService == null ? (long?)null : await auditService.LogPogingAsync(auditEntry);

                // TODO(#997-vervolg): het écht opslaan van het teruggekregen PublicMatchId in de
                // audit vereist een uitbreiding van ISportlinkMutationAuditService.VoltooiAsync
                // (raakt beide tiers) — niet nodig zolang dit pad altijd "DryRunLocked" teruggeeft
                // (ClubMatchLiveBevestigd = false in SportlinkClubClient).
                var mutationResult = await sportlinkClient!.CreateClubMatchAsync(RolNaam, aanvraag);
                return await SportlinkEndpointSupport.RondMutatieAfAsync(
                    mutationResult, auditService, auditId, r => r,
                    r => new OkObjectResult(new OefenwedstrijdAanmaakResultaat(
                        r.IsSuccess, r.Violations, r.IsDryRun, r.IsForcedDryRun, r.PublicMatchId,
                        omschrijving, aanvraag.PublicHomeTeamId, aanvraag.AgeClassCode, facilityId, veldNaam, waarschuwingen)));
            });

    /// <summary>
    /// <c>GET /api/sportlink/club-match/dryrun-status</c> (#1427) — de actuele dry-run-stand voor de
    /// banner op "Wedstrijd aanmaken". Leest alleen de club-instelling, geen Sportlink-aanroep. Eigen
    /// endpoint achter de Wedstrijdzaken-poort, omdat de health-endpoint admin-only is.
    /// </summary>
    [Function("SportlinkClubMatchDryRunStatusGet")]
    public static Task<IActionResult> GetDryRunStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/club-match/dryrun-status")] HttpRequest req,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SportlinkClubMatchDryRunStatusGet"), "dry-run-status ophalen",
            _ => Task.FromResult<IActionResult>(new OkObjectResult(new { DryRun = SportlinkEndpointSupport.IsDryRunActief() })));

    /// <summary>
    /// <c>GET /api/sportlink/club-match/picklists</c> — de twee ondersteunende Sportlink-picklists
    /// (Teams + Location). Sinds #1116 niet meer door het formulier gebruikt (teams en velden komen
    /// uit onze eigen database); blijft bestaan als diagnostisch endpoint voor de mens die de
    /// ClubMatch-body live gaat bevestigen — om te zien welke ID-vorm Sportlink Club hanteert en of
    /// die overeenkomt met <c>his.teams.teamcode</c>. Read-only en persoonsgegevensvrij.
    /// </summary>
    [Function("SportlinkClubMatchPickListsGet")]
    public static Task<IActionResult> GetPickLists(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/club-match/picklists")] HttpRequest req,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SportlinkClubMatchPickListsGet"), "oefenwedstrijd-picklists ophalen",
            async _ =>
            {
                var toggleFout = SportlinkEndpointSupport.ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;
                var (sportlinkClient, clientFout) = SportlinkEndpointSupport.ClientOfFout(context);
                if (clientFout != null) return clientFout;

                var result = await sportlinkClient!.GetClubMatchPickListsAsync(RolNaam);
                var fout = VertaalStatusNaarFout(result.Status);
                if (fout != null) return fout;

                return new OkObjectResult(result.Data ?? new SportlinkClubMatchPickLists(
                    Array.Empty<SportlinkPickListItem>(), Array.Empty<SportlinkPickListItem>()));
            });

    private static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status)
        => SportlinkEndpointSupport.VertaalStatusNaarFout(status);
}
