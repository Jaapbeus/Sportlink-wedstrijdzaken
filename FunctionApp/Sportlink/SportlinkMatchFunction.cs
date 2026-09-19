using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Planner.Shared.Integrations.SportlinkClub;
using SportlinkFunction.Admin;
using SportlinkFunction.Integrations.SportlinkClub;

namespace SportlinkFunction.Sportlink;

/// <summary>
/// SQL Server-tegenhanger van <c>FunctionApp.Postgres/Sportlink/SportlinkMatchFunction.cs</c>
/// (#1266, epic #986).
/// <para>
/// <c>GET /api/sportlink/match/{wedstrijdcode}</c> (#991) — read-only paneel-endpoint voor
/// Dagplanning, en <c>GET .../public-match-id</c> (#989) — lichtgewicht variant voor de
/// "Open in Sportlink"-deep-link-knop. Daarnaast de vier mutatie-endpoints #992 (kleedkamers),
/// #993 (veld), #994 (officials) en #995 (wijzigingsverzoek).
/// </para>
/// <para>
/// <b>Geen gedupliceerde beslislogica.</b> De toggle+EgressGuard-controle, de statusvertaling, de
/// dry-run-polariteit en de audit-afronding staan in <see cref="SportlinkEndpointCore"/>
/// (Planner.Shared) en lopen hier via <see cref="SportlinkEndpointSupport"/>. Wat per tier
/// verschilt is uitsluitend de databasetoegang (<see cref="SportlinkPublicMatchIdRepository"/>,
/// <see cref="SqlConnection"/>) — conform docs/ARCHITECTUUR-DATABASE-TIERS.md §2.
/// </para>
/// </summary>
public static class SportlinkMatchFunction
{
    private const string RolNaam = SportlinkEndpointSupport.RolWedstrijdzaken;

    [Function("SqlSportlinkMatchGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/match/{wedstrijdcode}")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkMatchGet"), "sportlink-match ophalen",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                var (voorbereidFout, publicMatchId) = await BereidPublicMatchIdVoorAsync(
                    sportlinkClient, wedstrijdcodeValue, clubCode);
                if (voorbereidFout != null) return voorbereidFout;

                var matchResult = await sportlinkClient!.GetMatchAsync(RolNaam, publicMatchId!);
                var matchFout = VertaalStatusNaarFout(matchResult.Status);
                if (matchFout != null) return matchFout;
                if (matchResult.Data == null)
                    return new NotFoundObjectResult(new { error = "Sportlink kent dit PublicMatchId niet (meer)." });

                return new OkObjectResult(matchResult.Data);
            });

    /// <summary>
    /// <c>GET /api/sportlink/match/{wedstrijdcode}/public-match-id</c> (#989, epic #986) —
    /// lichtgewicht variant voor de deep-link-knop in Dagplanning: geeft alleen het
    /// <c>PublicMatchId</c> terug (cache-first, reverse-lookup bij een cache-miss) zonder de
    /// volledige <c>Match</c>-aanroep bij Sportlink te doen — die extra aanroep is voor een
    /// deep-link niet nodig.
    /// </summary>
    [Function("SqlSportlinkMatchPublicMatchIdGet")]
    public static Task<IActionResult> GetPublicMatchId(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/match/{wedstrijdcode}/public-match-id")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkMatchPublicMatchIdGet"), "sportlink-publicmatchid ophalen",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                var (fout, publicMatchId) = await BereidPublicMatchIdVoorAsync(sportlinkClient, wedstrijdcodeValue, clubCode);
                if (fout != null) return fout;

                return new OkObjectResult(new { PublicMatchId = publicMatchId });
            });

    /// <summary>
    /// <c>PUT /api/sportlink/match/{wedstrijdcode}/dressingrooms</c> (#992, epic #986) — eerste
    /// echte Sportlink-mutatie vanuit deze app. Volgorde: PublicMatchId resolven → huidige
    /// match-status ophalen (voor de guardrail) → <see cref="SportlinkMutationGuard"/> → audit
    /// "Pending" loggen → mutatie uitvoeren → audit voltooien met het echte resultaat.
    /// </summary>
    [Function("SqlSportlinkMatchDressingRoomsPut")]
    public static Task<IActionResult> PutDressingRooms(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sportlink/match/{wedstrijdcode}/dressingrooms")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkMatchDressingRoomsPut"), "sportlink-kleedkamers wijzigen",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                var dto = await SportlinkEndpointSupport.LeesBodyAsync<KleedkamersDto>(req);

                return await ExecuteMutationAsync(
                    req, sportlinkClient, wedstrijdcodeValue, clubCode, "UpdateMatchDressingRooms",
                    SportlinkMutationSoort.Kleedkamers, dto, context,
                    (publicMatchId, match) => sportlinkClient!.UpdateDressingRoomsAsync(
                        RolNaam, publicMatchId,
                        BouwKleedkamerId(match.MatchField?.FacilityId, dto?.HomeDressingRoomId),
                        BouwKleedkamerId(match.MatchField?.FacilityId, dto?.AwayDressingRoomId),
                        BouwKleedkamerId(match.MatchField?.FacilityId, dto?.OfficialDressingRoomId)),
                    naarMutatieResultaat: r => r);
            });

    /// <summary>
    /// <c>PUT /api/sportlink/match/{wedstrijdcode}/field</c> (#993, epic #986) — veld(deel)
    /// wijzigen. <c>IsForceUpdate</c> staat altijd hard op <c>false</c>: de semantiek van die vlag
    /// is niet bevestigd (zie issue #993), dus geen enkel pad in deze app mag hem op <c>true</c>
    /// zetten totdat een mens dit live heeft geverifieerd.
    /// </summary>
    [Function("SqlSportlinkMatchFieldPut")]
    public static Task<IActionResult> PutField(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sportlink/match/{wedstrijdcode}/field")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkMatchFieldPut"), "sportlink-veld wijzigen",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                var dto = await SportlinkEndpointSupport.LeesBodyAsync<VeldDto>(req);

                return await ExecuteMutationAsync(
                    req, sportlinkClient, wedstrijdcodeValue, clubCode, "UpdateMatchField",
                    SportlinkMutationSoort.Veld, dto, context,
                    (publicMatchId, _) => sportlinkClient!.UpdateFieldAsync(
                        RolNaam, publicMatchId, dto?.FieldId, dto?.FieldSize, dto?.FieldOffset, isForceUpdate: false),
                    naarMutatieResultaat: r => r);
            });

    /// <summary>
    /// <c>PUT /api/sportlink/match/{wedstrijdcode}/officials</c> (#994, epic #986) — officials
    /// (scheidsrechter/assistenten) toewijzen. <b>ONBEVESTIGD, altijd code-gelockt:</b> deze
    /// mutatie loopt via <see cref="ISportlinkClubClient.AssignOfficialsAsync"/>, dat intern
    /// <c>forceDryRun: true</c> gebruikt totdat een mens een live trace heeft gedaan (zie
    /// <c>docs/SPORTLINK-WEB-EXTENSION.md</c> §4.2/§4.4) — ONAFHANKELIJK van de club-instelling
    /// <c>sportlinkDryRun</c>. AVG: de DTO bevat uitsluitend positie + een door de beheerder
    /// ingevoerde relatiecode/persoons-ID, nooit een naam — er wordt geen enkel Sportlink-zoek-
    /// /personendetail-endpoint aangeroepen (zie issue #994).
    /// </summary>
    [Function("SqlSportlinkMatchOfficialsPut")]
    public static Task<IActionResult> PutOfficials(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sportlink/match/{wedstrijdcode}/officials")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkMatchOfficialsPut"), "sportlink-officials toewijzen",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                var dto = await SportlinkEndpointSupport.LeesBodyAsync<OfficialsDto>(req);

                var toewijzingen = (dto?.Officials ?? new List<OfficialToewijzingDto>())
                    .Where(o => !string.IsNullOrWhiteSpace(o.OfficialPosition) && !string.IsNullOrWhiteSpace(o.PersoonId))
                    .Select(o => new SportlinkOfficialToewijzing(o.OfficialPosition!, o.PersoonId!))
                    .ToList();

                return await ExecuteMutationAsync(
                    req, sportlinkClient, wedstrijdcodeValue, clubCode, "MatchOfficialsAction",
                    SportlinkMutationSoort.Officials, dto, context,
                    (publicMatchId, _) => sportlinkClient!.AssignOfficialsAsync(RolNaam, publicMatchId, toewijzingen),
                    naarMutatieResultaat: r => r);
            });

    // NIET VERDER BOUWEN ZONDER LIVE BEVESTIGING DOOR DE EIGENAAR (#995, Aanpak-stap 1: body van
    // beide PUT's en de bevestigingsvlag vastleggen). Dit endpoint is uitsluitend stap 1
    // (valideren) van Sportlinks tweestaps flow — er bestaat bewust geen stap 2 (bevestigen): geen
    // endpoint, geen client-methode, geen UI-knop daarvoor.
    /// <summary>
    /// <c>PUT /api/sportlink/match/{wedstrijdcode}/change-request</c> (#995, epic #986) —
    /// wijzigingsverzoek datum/tijd/accommodatie. <b>ONBEVESTIGD, altijd code-gelockt:</b> deze
    /// mutatie loopt via <see cref="ISportlinkClubClient.RequestMatchChangeAsync"/>, dat intern
    /// <c>forceDryRun: true</c> gebruikt totdat een mens een live trace heeft gedaan (zie
    /// <c>docs/SPORTLINK-WEB-EXTENSION.md</c> §4.4) — ONAFHANKELIJK van de club-instelling
    /// <c>sportlinkDryRun</c>. Dit is de enige mutatiesoort die een ECHTE tegenstander raakt
    /// (Sportlink stuurt bij bevestiging een goedkeuringsverzoek naar de tegenstander) — zelfs stap
    /// 1 (deze) kan in werkelijkheid al het gevaarlijke moment zijn, vandaar dat de code-lock hier
    /// extra belangrijk is.
    /// </summary>
    [Function("SqlSportlinkMatchChangeRequestPut")]
    public static Task<IActionResult> PutChangeRequest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sportlink/match/{wedstrijdcode}/change-request")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        SportlinkEndpointSupport.ExecuteWedstrijdzakenAsync(req, context.GetLogger("SqlSportlinkMatchChangeRequestPut"), "sportlink-wijzigingsverzoek datum/tijd/accommodatie",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                var dto = await SportlinkEndpointSupport.LeesBodyAsync<ChangeRequestDto>(req);

                // Zelfde verplicht-veld-patroon als SportlinkChangeRequestFunction.PutAction
                // (Remarks bij DENY): een wijzigingsverzoek zonder toelichting is voor de
                // tegenstander niet te beoordelen.
                if (string.IsNullOrWhiteSpace(dto?.Toelichting))
                    return new BadRequestObjectResult(new { error = "Toelichting is verplicht bij een wijzigingsverzoek." });

                DateOnly? nieuweDatum = null;
                if (!string.IsNullOrWhiteSpace(dto.NieuweDatum))
                {
                    if (!DateOnly.TryParse(dto.NieuweDatum, out var datumWaarde))
                        return new BadRequestObjectResult(new { error = "NieuweDatum moet een geldige datum zijn (yyyy-MM-dd)." });
                    nieuweDatum = datumWaarde;
                }

                TimeOnly? nieuweStartTijd = null;
                if (!string.IsNullOrWhiteSpace(dto.NieuweStartTijd))
                {
                    if (!TimeOnly.TryParse(dto.NieuweStartTijd, out var tijdWaarde))
                        return new BadRequestObjectResult(new { error = "NieuweStartTijd moet een geldige tijd zijn (HH:mm)." });
                    nieuweStartTijd = tijdWaarde;
                }

                return await ExecuteMutationAsync(
                    req, sportlinkClient, wedstrijdcodeValue, clubCode, "UpdateMatchDetails:ChangeRequest",
                    SportlinkMutationSoort.DatumTijdAccommodatie, dto, context,
                    (publicMatchId, _) => sportlinkClient!.RequestMatchChangeAsync(
                        RolNaam, publicMatchId, nieuweDatum, nieuweStartTijd, dto.NieuweFacilityId, dto.Toelichting!),
                    naarMutatieResultaat: r => r.Mutatie);
            });

    // Live vastgesteld (2026-09-06, netwerktrace door de eigenaar): Sportlink verwacht
    // "{FacilityId}-DRESSINGROOM-{n}", geen los kleedkamernummer. De DTO-veldnamen blijven
    // ...DressingRoomId (wire-compatibel met BlazorAdmin), maar de waarde die de UI stuurt is het
    // losse nummer — deze helper bouwt de echte identifier.
    internal static string? BouwKleedkamerId(string? facilityId, string? kleedkamerNummer)
    {
        if (string.IsNullOrWhiteSpace(kleedkamerNummer)) return kleedkamerNummer;
        if (string.IsNullOrWhiteSpace(facilityId)) return kleedkamerNummer;
        return $"{facilityId}-DRESSINGROOM-{kleedkamerNummer}";
    }

    /// <summary>Audit-<c>resultaat</c> voor een mutatie-uitkomst (#998, uitgebreid #994) — één
    /// gedeelde regel in <see cref="SportlinkEndpointCore.BepaalAuditResultaat"/>.</summary>
    internal static string BepaalAuditResultaat(SportlinkMutationResult r) => SportlinkEndpointSupport.BepaalAuditResultaat(r);

    private sealed class KleedkamersDto
    {
        public string? HomeDressingRoomId { get; set; }
        public string? AwayDressingRoomId { get; set; }
        public string? OfficialDressingRoomId { get; set; }
    }

    private sealed class VeldDto
    {
        public string? FieldId { get; set; }
        public string? FieldSize { get; set; }
        public int? FieldOffset { get; set; }
    }

    /// <summary>#994: per positie een losse tekstinvoer (relatiecode/persoons-ID) — geen zoekfunctie,
    /// geen namen (AVG). <see cref="OfficialToewijzingDto.OfficialPosition"/>-waarden zijn ONBEVESTIGD,
    /// zie <see cref="SportlinkOfficialToewijzing"/>.</summary>
    private sealed class OfficialsDto
    {
        public List<OfficialToewijzingDto>? Officials { get; set; }
    }

    private sealed class OfficialToewijzingDto
    {
        public string? OfficialPosition { get; set; }
        public string? PersoonId { get; set; }
    }

    /// <summary>#995: datum/tijd als losse strings (niet <c>DateOnly</c>/<c>TimeOnly</c> op de DTO
    /// zelf) — consistent met de andere DTO's in dit bestand, en vermijdt een afhankelijkheid van
    /// Newtonsoft.Json's (on)ondersteuning van die typen. De handler hierboven parseert en
    /// valideert ze expliciet.</summary>
    private sealed class ChangeRequestDto
    {
        public string? NieuweDatum { get; set; }
        public string? NieuweStartTijd { get; set; }
        public string? NieuweFacilityId { get; set; }
        public string? Toelichting { get; set; }
    }

    /// <summary>Gedeelde stappen voor elke mutatie-actie op een bestaande wedstrijd: PublicMatchId
    /// resolven, huidige match ophalen (voor de guardrail), <see cref="SportlinkMutationGuard"/>,
    /// audit "Pending" loggen, <paramref name="mutationCall"/> uitvoeren, audit voltooien met het
    /// echte resultaat. Eén vertaalpunt voor #992/#993/#994/#995 — een losse kopie per endpoint zou
    /// het risico geven dat een nieuw endpoint de guard of de audit-log vergeet.</summary>
    private static async Task<IActionResult> ExecuteMutationAsync<T>(
        HttpRequest req,
        ISportlinkClubClient? sportlinkClient,
        long wedstrijdcodeValue,
        string clubCode,
        string actie,
        SportlinkMutationSoort soort,
        object? waardeNaDto,
        FunctionContext context,
        Func<string, SportlinkMatch, Task<SportlinkClubResponse<T>>> mutationCall,
        Func<T, SportlinkMutationResult> naarMutatieResultaat)
        where T : class
    {
        var (voorbereidFout, publicMatchId) = await BereidPublicMatchIdVoorAsync(sportlinkClient, wedstrijdcodeValue, clubCode);
        if (voorbereidFout != null) return voorbereidFout;

        var matchResult = await sportlinkClient!.GetMatchAsync(RolNaam, publicMatchId!);
        var matchFout = VertaalStatusNaarFout(matchResult.Status);
        if (matchFout != null) return matchFout;
        if (matchResult.Data == null)
            return new NotFoundObjectResult(new { error = "Sportlink kent dit PublicMatchId niet (meer)." });

        var guard = SportlinkMutationGuard.MagMuteren(matchResult.Data, soort);

        var auditService = context.InstanceServices.GetService<ISportlinkMutationAuditService>();
        var triggerdDoor = EasyAuthHelper.GetAuditActor(req);
        // #998: WaardeVoor bevat alleen niet-persoonsgebonden velden die al in SportlinkMatch
        // zitten. Doel: een seizoen aan auditdata verzamelen vóórdat MatchStatus eventueel een
        // harde guard-blokkade wordt (zie SportlinkMutationGuard).
        var auditEntry = new SportlinkMutationAuditEntry(
            clubCode, RolNaam, triggerdDoor, publicMatchId!, actie,
            WaardeVoor: JsonConvert.SerializeObject(new
            {
                matchResult.Data.TaskStatus,
                matchResult.Data.MatchStatus,
                matchResult.Data.IsCanceledMatch,
                matchResult.Data.IsConceptMatch,
                FacilityId = matchResult.Data.MatchField?.FacilityId,
                FacilityName = matchResult.Data.MatchField?.Name
            }),
            WaardeNa: JsonConvert.SerializeObject(waardeNaDto),
            CorrelationId: null);
        var auditId = auditService == null ? (long?)null : await auditService.LogPogingAsync(auditEntry);

        if (!guard.IsToegstaan)
        {
            if (auditId.HasValue) await auditService!.VoltooiAsync(auditId.Value, "Geblokkeerd", guard.Reden);
            return new ObjectResult(new { error = guard.Reden }) { StatusCode = 409 };
        }

        var mutationResult = await mutationCall(publicMatchId!, matchResult.Data);
        return await SportlinkEndpointSupport.RondMutatieAfAsync(
            mutationResult, auditService, auditId, naarMutatieResultaat, data => new OkObjectResult(data));
    }

    /// <summary>Gedeelde stappen van alle endpoints hierboven: toggle-check, EgressGuard,
    /// wedstrijd-lookup en PublicMatchId-cache/reverse-lookup. Geen van de aanroepers heeft de
    /// DB-connectie na afloop nog nodig (de resterende stap is telkens een HTTP-aanroep naar
    /// Sportlink), dus de connectie leeft uitsluitend binnen deze methode.</summary>
    private static async Task<(IActionResult? Fout, string? PublicMatchId)>
        BereidPublicMatchIdVoorAsync(ISportlinkClubClient? sportlinkClient, long wedstrijdcodeValue, string clubCode)
    {
        var toggleFout = SportlinkEndpointSupport.ControleerToggleEnEgress();
        if (toggleFout != null) return (toggleFout, null);
        if (sportlinkClient == null)
            return (SportlinkEndpointSupport.ClientNietGeconfigureerdFout(), null);

        await using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
        await connection.OpenAsync();

        var wedstrijd = await SportlinkPublicMatchIdRepository.ZoekWedstrijdAsync(connection, wedstrijdcodeValue, clubCode);
        if (wedstrijd == null)
            return (new NotFoundObjectResult(new { error = $"Wedstrijd met wedstrijdcode {wedstrijdcodeValue} niet gevonden." }), null);

        var publicMatchId = await SportlinkPublicMatchIdRepository.LeesUitCacheAsync(connection, wedstrijdcodeValue, clubCode);
        if (publicMatchId != null)
            return (null, publicMatchId);

        var lookup = await sportlinkClient.ResolvePublicMatchIdAsync(RolNaam, wedstrijd.Wedstrijdnummer, wedstrijd.Datum);
        var lookupFout = VertaalStatusNaarFout(lookup.Status);
        if (lookupFout != null)
            return (lookupFout, null);
        if (lookup.Data == null)
            return (new NotFoundObjectResult(new { error = "Wedstrijd nog niet bekend bij Sportlink voor deze datum." }), null);

        publicMatchId = lookup.Data.PublicMatchId;
        await SportlinkPublicMatchIdRepository.SchrijfInCacheAsync(connection, wedstrijdcodeValue, clubCode, publicMatchId);
        return (null, publicMatchId);
    }

    private static IActionResult? VertaalStatusNaarFout(SportlinkClubCallStatus status)
        => SportlinkEndpointSupport.VertaalStatusNaarFout(status);
}
