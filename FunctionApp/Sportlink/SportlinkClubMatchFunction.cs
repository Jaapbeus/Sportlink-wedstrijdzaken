using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Endpoints.Sportlink;
using Planner.Shared.Integrations.SportlinkClub;
using static Planner.Endpoints.Sportlink.ClubMatchEndpointCore;
using SportlinkFunction.Admin;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Sportlink/SportlinkClubMatchFunction.cs</c>
/// (#1266, epic #986).
/// <para>
/// Oefenwedstrijd ("clubwedstrijd") aanmaken bij Sportlink (#997; formulier #1116, contract live
/// bevestigd #1427). Uitslag vastleggen is bewust niet gebouwd;
/// verwijderen (#1440) staat hard op dry-run tot de eigenaar het contract live bevestigt.
/// </para>
/// <para>
/// <b>Sinds #1427 live bevestigd.</b> Het formulier levert datum, tijd, duur, eigen team (naam
/// uit onze eigen database, of vrije tekst), tegenstander en veld. De server haalt Sportlinks vier
/// aanmaaklijsten op en <see cref="Planner.Shared.Integrations.SportlinkClub.ClubMatchAanvraagBouwer"/>
/// maakt daaruit de body zoals Sportlinks eigen formulier hem verstuurt: het <c>T…</c>-team-ID,
/// leeftijdscategorie, spelactiviteit, veld (<c>SubFacilityId</c>) en wedstrijdnummer. Wat niet
/// eenduidig af te leiden is, valt terug op <c>ClubMatchDefaults</c> met een <i>waarschuwing</i>;
/// een team of veld dat Sportlink niet kent is een 400. Dit bestand houdt alleen de databasevraag
/// en de HTTP-aansluiting — zie <c>Planner.Endpoints/Sportlink/ClubMatchEndpointCore.cs</c>.
/// </para>
/// <para>
/// <b>Structureel anders dan de andere Sportlink-mutatiefuncties:</b> <see
/// cref="SportlinkMatchFunction"/> en <see cref="SportlinkChangeRequestFunction"/> werken op een AL
/// BESTAANDE wedstrijd (eerst opzoeken, dan pas de guard aanroepen). Hier bestaat er vooraf geen
/// <c>PublicMatchId</c>, geen <c>wedstrijdcode</c>, geen <c>SportlinkMatch</c> om te guarden — dus
/// GEEN <see cref="SportlinkMutationGuard"/>-check. In plaats daarvan gelden alleen ONZE EIGEN
/// regels (de toggle + <c>EgressGuard.ExternalIntegrationsAllowed()</c>), zelfde patroon als
/// <see cref="SportlinkChangeRequestFunction"/>.
/// </para>
/// <para>
/// <b>Audit-placeholder:</b> <see cref="SportlinkMutationAuditEntry"/> vereist een verplicht,
/// niet-leeg <c>PublicMatchId</c>-veld — dat bestaat nog niet bij het aanmaken. De Pending-rij
/// gebruikt daarom de placeholder-waarde <c>"NIEUW"</c>, met een gegenereerde GUID in
/// <c>CorrelationId</c> om de Pending- en Voltooid-rij aan elkaar te koppelen.
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
    [Function("SqlSportlinkClubMatchPost")]
    public static Task<IActionResult> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sportlink/club-match")] HttpRequest req,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkClubMatchPost"), "oefenwedstrijd aanmaken",
            async clubCode =>
            {
                var log = context.GetLogger("SqlSportlinkClubMatchPost");

                var toggleFout = SportlinkEndpointSupport.ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;

                var dto = await SportlinkEndpointSupport.LeesBodyAsync<OefenwedstrijdAanmakenDto>(req);
                var validatieFout = Valideer(dto);
                if (validatieFout != null) return validatieFout;

                var (sportlinkClient, clientFout) = SportlinkEndpointSupport.ClientOfFout(context);
                if (clientFout != null) return clientFout;

                var cs = SystemUtilities.DatabaseConfig.ConnectionString;
                var (gegevens, _, gegevensFout) = await LosGegevensOpAsync(dto!,
                    naam => SportlinkClubMatchRepository.GetTeamKoppelingAsync(clubCode, naam, cs),
                    nr => SportlinkClubMatchRepository.GetActiefVeldNaamAsync(clubCode, nr, cs),
                    () => SportlinkClubMatchRepository.GetSpelactiviteitAsync(clubCode, cs),
                    SystemUtilities.AppSettings.GetSetting("accommodatie"));
                if (gegevensFout != null) return gegevensFout;

                var (aanvraag, bouwFout, waarschuwingen) = await BouwAanvraagAsync(
                    sportlinkClient!, RolNaam, dto!, gegevens!,
                    datum => SportlinkClubMatchRepository.ReserveerVolgnummerAsync(clubCode, datum, cs), log);
                if (bouwFout != null) return bouwFout;

                var auditService = context.InstanceServices.GetService<ISportlinkMutationAuditService>();
                if (auditService == null) return SportlinkEndpointSupportCore.AuditNietBeschikbaarFout();
                var triggerdDoor = EasyAuthHelper.GetAuditActor(req);
                var correlationId = Guid.NewGuid().ToString();
                var auditEntry = new SportlinkMutationAuditEntry(
                    clubCode, RolNaam, triggerdDoor, AuditPublicMatchIdPlaceholder, "CreateClubMatch",
                    WaardeVoor: null,
                    WaardeNa: JsonConvert.SerializeObject(new { Invoer = dto, Aanvraag = aanvraag, VeldNaam = gegevens!.VeldNaam, Waarschuwingen = waarschuwingen }),
                    CorrelationId: correlationId);
                var auditId = auditService == null ? (long?)null : await auditService.LogPogingAsync(auditEntry);

                var mutationResult = await sportlinkClient!.CreateClubMatchAsync(RolNaam, aanvraag!);
                return await SportlinkEndpointSupport.RondMutatieAfAsync(
                    mutationResult, auditService, auditId, r => r,
                    r => new OkObjectResult(Resultaat(r, aanvraag!, gegevens!.VeldNaam, waarschuwingen)));
            });

    /// <summary>
    /// <c>DELETE /api/sportlink/club-match/{publicMatchId}</c> (#1440) — een clubwedstrijd verwijderen.
    /// Contract uit Sportlinks publieke frontend-bundle en sinds #1458 live bevestigd; de aanroep
    /// volgt de club-instelling <c>sportlinkDryRun</c>. Orkestratie en guard: <see
    /// cref="Planner.Endpoints.Sportlink.ClubMatchVerwijderCore"/>; hier alleen de route en de audit.
    /// </summary>
    [Function("SqlSportlinkClubMatchDelete")]
    public static Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sportlink/club-match/{publicMatchId}")] HttpRequest req,
        string publicMatchId,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkClubMatchDelete"), "oefenwedstrijd verwijderen",
            clubCode => ClubMatchVerwijderCore.VerwijderAsync(
                publicMatchId, SportlinkEndpointSupport.ControleerToggleEnEgress, () => SportlinkEndpointSupport.ClientOfFout(context), RolNaam,
                context.InstanceServices.GetService<ISportlinkMutationAuditService>() is { } audit
                    ? new(clubCode, EasyAuthHelper.GetAuditActor(req), e => audit.LogPogingAsync(e), (id, r, s) => audit.VoltooiAsync(id, r, s))
                    : null));

    /// <summary>
    /// <c>GET /api/sportlink/club-match/dryrun-status</c> (#1427) — de actuele dry-run-stand voor de
    /// banner op "Wedstrijd aanmaken". Leest alleen de club-instelling, geen Sportlink-aanroep. Eigen
    /// endpoint achter de Wedstrijdzaken-poort, omdat de health-endpoint admin-only is.
    /// </summary>
    [Function("SqlSportlinkClubMatchDryRunStatusGet")]
    public static Task<IActionResult> GetDryRunStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/club-match/dryrun-status")] HttpRequest req,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkClubMatchDryRunStatusGet"), "dry-run-status ophalen",
            _ => Task.FromResult<IActionResult>(new OkObjectResult(new { DryRun = SportlinkEndpointSupport.IsDryRunActief() })));

    /// <summary>
    /// <c>GET /api/sportlink/club-match/formulier</c> (#1437) — de voorinvulling voor "Wedstrijd aanmaken":
    /// per actief team de Sportlink-leeftijdscategorie, duur en velddeel (uit de speeltijden), plus
    /// Sportlinks leeftijdscategorielijst. Achter de Wedstrijdzaken-poort: de speeltijden-API is
    /// admin-only en dus niet bruikbaar voor deze pagina. Is Sportlink niet bereikbaar, dan komen de
    /// teamgegevens zonder lijst terug (<c>sportlinkBeschikbaar = false</c>).
    /// </summary>
    [Function("SqlSportlinkClubMatchFormulierGet")]
    public static Task<IActionResult> GetFormulier(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/club-match/formulier")] HttpRequest req,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkClubMatchFormulierGet"), "oefenwedstrijd-formulier ophalen",
            clubCode => FormulierAsync(
                () => SportlinkClubMatchRepository.GetFormulierGegevensAsync(clubCode, SystemUtilities.DatabaseConfig.ConnectionString),
                () => SportlinkEndpointSupport.ControleerToggleEnEgress() == null
                      && SportlinkEndpointSupport.ClientOfFout(context) is { Fout: null } c ? c.Client : null,
                RolNaam, context.GetLogger("SqlSportlinkClubMatchFormulierGet")));

    /// <summary>
    /// <c>GET /api/sportlink/club-match/picklists</c> — de twee ondersteunende Sportlink-picklists
    /// (Teams + Location). Sinds #1116 niet meer door het formulier gebruikt (teams en velden komen
    /// uit onze eigen database); blijft bestaan als diagnostisch endpoint voor de mens die de
    /// ClubMatch-body live gaat bevestigen. Read-only en persoonsgegevensvrij.
    /// </summary>
    [Function("SqlSportlinkClubMatchPickListsGet")]
    public static Task<IActionResult> GetPickLists(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/club-match/picklists")] HttpRequest req,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkClubMatchPickListsGet"), "oefenwedstrijd-picklists ophalen",
            _ => PickListsAsync(
                SportlinkEndpointSupport.ControleerToggleEnEgress, () => SportlinkEndpointSupport.ClientOfFout(context), RolNaam));
}
