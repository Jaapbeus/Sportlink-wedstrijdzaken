using System.Collections.Concurrent;
using FunctionApp.Postgres.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.Integrations.SportlinkClub;

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
    private const int MaxDuurMinuten = 240;

    /// <summary>
    /// De Sportlink-locatiepicklist verandert praktisch nooit (accommodaties van de club). Eén keer
    /// per uur per club ophalen is ruim genoeg en voorkomt twee extra Sportlink-GETs per ingevoerde
    /// oefenwedstrijd. Bewust in-memory: de Consumption-host recyclet toch, en een miss kost alleen
    /// één read-only aanroep.
    /// </summary>
    private static readonly TimeSpan LocatieCacheDuur = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, (DateTime OpgehaaldUtc, IReadOnlyList<SportlinkPickListItem> Locaties)> LocatieCache = new();

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
                var log = context.GetLogger("SportlinkClubMatchPost");

                var toggleFout = SportlinkEndpointSupport.ControleerToggleEnEgress();
                if (toggleFout != null) return toggleFout;

                var dto = await SportlinkEndpointSupport.LeesBodyAsync<OefenwedstrijdAanmakenDto>(req);
                var validatieFout = Valideer(dto);
                if (validatieFout != null) return validatieFout;

                var (sportlinkClient, clientFout) = SportlinkEndpointSupport.ClientOfFout(context);
                if (clientFout != null) return clientFout;

                var cs = PostgresDatabaseConfig.ConnectionString;
                var team = await SportlinkClubMatchRepository.GetTeamKoppelingAsync(clubCode, dto!.TeamNaam!, cs);
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
                var facilityId = await BepaalFacilityIdAsync(sportlinkClient!, clubCode, accommodatie, waarschuwingen, log);

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
                var mutationResult = await sportlinkClient.CreateClubMatchAsync(RolNaam, aanvraag);
                return await SportlinkEndpointSupport.RondMutatieAfAsync(
                    mutationResult, auditService, auditId, r => r,
                    r => new OkObjectResult(new OefenwedstrijdAanmaakResultaat(
                        r.IsSuccess, r.Violations, r.IsDryRun, r.IsForcedDryRun, r.PublicMatchId,
                        omschrijving, aanvraag.PublicHomeTeamId, aanvraag.AgeClassCode, facilityId, veldNaam, waarschuwingen)));
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

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
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkClubMatchPickListsGet"), "oefenwedstrijd-picklists ophalen",
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
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

    /// <summary>Invoer van het formulier (#1116) — alleen wat een mens snel kan intikken; de Sportlink-ID's leidt de server af.</summary>
    internal sealed class OefenwedstrijdAanmakenDto
    {
        public DateTime? MatchDateTime { get; set; }
        public int? Duration { get; set; }
        public string? TeamNaam { get; set; }
        public string? Tegenstander { get; set; }
        public int? VeldNummer { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// Respons van <c>POST /api/sportlink/club-match</c>: het generieke mutatieresultaat plus wat de
    /// server uit teamnaam en instellingen heeft afgeleid, zodat de beheerder ziet wat er
    /// (gesimuleerd) naar Sportlink zou gaan. Spiegelt <c>BlazorAdmin.Models.OefenwedstrijdResultaatDto</c>.
    /// </summary>
    internal sealed record OefenwedstrijdAanmaakResultaat(
        bool IsSuccess,
        IReadOnlyList<string>? Violations,
        bool IsDryRun,
        bool IsForcedDryRun,
        string? PublicMatchId,
        string Omschrijving,
        string? SportlinkTeamId,
        string? AgeClassCode,
        string? FacilityId,
        string? VeldNaam,
        IReadOnlyList<string> Waarschuwingen);

    /// <summary>Invoervalidatie — <c>null</c> als de aanvraag bruikbaar is, anders een 400 met de reden.</summary>
    internal static IActionResult? Valideer(OefenwedstrijdAanmakenDto? dto)
    {
        if (dto?.MatchDateTime == null)
            return new BadRequestObjectResult(new { error = "MatchDateTime is verplicht." });
        if (string.IsNullOrWhiteSpace(dto.TeamNaam))
            return new BadRequestObjectResult(new { error = "TeamNaam is verplicht." });
        if (string.IsNullOrWhiteSpace(dto.Tegenstander))
            return new BadRequestObjectResult(new { error = "Tegenstander is verplicht." });
        if (dto.Duration is < 1 or > MaxDuurMinuten)
            return new BadRequestObjectResult(new { error = $"Duration moet tussen 1 en {MaxDuurMinuten} minuten liggen." });
        return null;
    }

    /// <summary>
    /// Omschrijving zoals die naar Sportlink gaat: de eigen tekst van de beheerder, of anders een
    /// standaardtekst met team, tegenstander en — zolang <c>FieldId</c> nog niet wordt meegestuurd —
    /// het gekozen veld, zodat dat in Sportlink Club in ieder geval leesbaar is.
    /// </summary>
    internal static string BouwOmschrijving(string? eigenTekst, string teamNaam, string tegenstander, string? veldNaam)
    {
        if (!string.IsNullOrWhiteSpace(eigenTekst)) return eigenTekst.Trim();
        var basis = $"Oefenwedstrijd {teamNaam} - {tegenstander.Trim()}";
        return string.IsNullOrWhiteSpace(veldNaam) ? basis : $"{basis} ({veldNaam})";
    }

    /// <summary>
    /// Zoekt de eigen accommodatie (club-instelling <c>accommodatie</c>) op naam in de Sportlink-
    /// locatiepicklist. Eerst exact (hoofdletter- en spatie-ongevoelig); lukt dat niet, dan één
    /// unieke gedeeltelijke match (de ene naam bevat de andere). Meerdere of geen treffers → <c>null</c>:
    /// beter leeg dan de verkeerde locatie.
    /// </summary>
    internal static string? ZoekFacilityId(IEnumerable<SportlinkPickListItem> locaties, string? accommodatie)
    {
        if (string.IsNullOrWhiteSpace(accommodatie)) return null;
        var gezocht = accommodatie.Trim();
        var kandidaten = locaties
            .Where(l => !string.IsNullOrWhiteSpace(l.Id) && !string.IsNullOrWhiteSpace(l.Naam))
            .Select(l => (l.Id!, Naam: l.Naam!.Trim()))
            .ToList();

        var exact = kandidaten.Where(k => string.Equals(k.Naam, gezocht, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0].Item1;
        if (exact.Count > 1) return null;

        var gedeeltelijk = kandidaten
            .Where(k => k.Naam.Contains(gezocht, StringComparison.OrdinalIgnoreCase)
                     || gezocht.Contains(k.Naam, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return gedeeltelijk.Count == 1 ? gedeeltelijk[0].Item1 : null;
    }

    private static async Task<string?> BepaalFacilityIdAsync(
        ISportlinkClubClient client, string clubCode, string? accommodatie, List<string> waarschuwingen, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(accommodatie))
        {
            waarschuwingen.Add("Club-instelling 'accommodatie' is leeg — FacilityId blijft leeg.");
            return null;
        }

        var locaties = await HaalLocatiesAsync(client, clubCode, log);
        if (locaties == null)
        {
            waarschuwingen.Add("Sportlink-locatielijst kon niet worden opgehaald — FacilityId blijft leeg.");
            return null;
        }

        var facilityId = ZoekFacilityId(locaties, accommodatie);
        if (facilityId == null)
            waarschuwingen.Add($"Accommodatie '{accommodatie}' niet (eenduidig) gevonden in de Sportlink-locatielijst — FacilityId blijft leeg.");
        return facilityId;
    }

    private static async Task<IReadOnlyList<SportlinkPickListItem>?> HaalLocatiesAsync(ISportlinkClubClient client, string clubCode, ILogger log)
    {
        if (LocatieCache.TryGetValue(clubCode, out var cached) && DateTime.UtcNow - cached.OpgehaaldUtc < LocatieCacheDuur)
            return cached.Locaties;

        var result = await client.GetClubMatchPickListsAsync(RolNaam);
        if (result.Status != SportlinkClubCallStatus.Ok || result.Data == null)
        {
            // Alleen de status loggen, nooit de foutmelding-body — die kan Sportlink-details bevatten.
            log.LogWarning("Sportlink-locatiepicklist niet beschikbaar (status {Status}); FacilityId blijft leeg.", result.Status);
            return null;
        }

        LocatieCache[clubCode] = (DateTime.UtcNow, result.Data.Locations);
        return result.Data.Locations;
    }

    private static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status)
        => SportlinkEndpointSupport.VertaalStatusNaarFout(status);
}
