using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Infrastructure;
using FunctionApp.Postgres.Integrations.SportlinkClub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Npgsql;
using Planner.Shared.Integrations.SportlinkClub;

namespace FunctionApp.Postgres.Sportlink;

/// <summary>
/// <c>GET /api/sportlink/match/{wedstrijdcode}</c> (#991, epic #986) — read-only paneel-endpoint
/// voor Dagplanning, en <c>GET .../public-match-id</c> (#989) — lichtgewicht variant voor de
/// "Open in Sportlink"-deep-link-knop. Eerste echte gebruik van <c>RequireWedstrijdzaken</c> (#988
/// Besluit 1: die granulaire rol-gating komt pas bij het eerste echte lees-/mutatie-endpoint).
/// <para>
/// Verbindt drie stukken die elk in een aparte issue/PR gebouwd zijn: de gedeelde
/// <see cref="ISportlinkClubClient"/> (#991/#998, <c>Planner.Shared</c>), de Postgres-tier
/// <see cref="PostgresSportlinkClubTokenStore"/> (#991) en de PublicMatchId-reverse-lookup-cache
/// (#991/#1016, <see cref="SportlinkPublicMatchIdRepository"/>).
/// </para>
/// </summary>
public static class SportlinkMatchFunction
{
    private const string RolNaam = "Wedstrijdzaken";

    [Function("SportlinkMatchGet")]
    public static Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/match/{wedstrijdcode}")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkMatchGet"), "sportlink-match ophalen",
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
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

    /// <summary>
    /// <c>GET /api/sportlink/match/{wedstrijdcode}/public-match-id</c> (#989, epic #986) —
    /// lichtgewicht variant voor de deep-link-knop in Dagplanning: geeft alleen het
    /// <c>PublicMatchId</c> terug (cache-first, reverse-lookup bij een cache-miss) zonder de
    /// volledige <c>Match</c>-aanroep bij Sportlink te doen — die extra aanroep is voor een
    /// deep-link niet nodig.
    /// </summary>
    [Function("SportlinkMatchPublicMatchIdGet")]
    public static Task<IActionResult> GetPublicMatchId(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sportlink/match/{wedstrijdcode}/public-match-id")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkMatchPublicMatchIdGet"), "sportlink-publicmatchid ophalen",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                var (fout, publicMatchId) = await BereidPublicMatchIdVoorAsync(sportlinkClient, wedstrijdcodeValue, clubCode);
                if (fout != null) return fout;

                return new OkObjectResult(new { PublicMatchId = publicMatchId });
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

    /// <summary>
    /// <c>PUT /api/sportlink/match/{wedstrijdcode}/dressingrooms</c> (#992, epic #986) — eerste
    /// echte Sportlink-mutatie vanuit deze app. Volgorde: PublicMatchId resolven → huidige
    /// match-status ophalen (voor de guardrail) → <see cref="SportlinkMutationGuard"/> → audit
    /// "Pending" loggen → mutatie uitvoeren → audit voltooien met het echte resultaat.
    /// </summary>
    [Function("SportlinkMatchDressingRoomsPut")]
    public static Task<IActionResult> PutDressingRooms(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sportlink/match/{wedstrijdcode}/dressingrooms")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkMatchDressingRoomsPut"), "sportlink-kleedkamers wijzigen",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                var dto = JsonConvert.DeserializeObject<KleedkamersDto>(
                    await new StreamReader(req.Body).ReadToEndAsync());

                return await ExecuteMutationAsync(
                    req, sportlinkClient, wedstrijdcodeValue, clubCode, "UpdateMatchDressingRooms",
                    SportlinkMutationSoort.Kleedkamers, dto, context,
                    publicMatchId => sportlinkClient!.UpdateDressingRoomsAsync(
                        RolNaam, publicMatchId, dto?.HomeDressingRoomId, dto?.AwayDressingRoomId, dto?.OfficialDressingRoomId));
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

    /// <summary>
    /// <c>PUT /api/sportlink/match/{wedstrijdcode}/field</c> (#993, epic #986) — veld(deel)
    /// wijzigen. <c>IsForceUpdate</c> staat altijd hard op <c>false</c>: de semantiek van die vlag
    /// is niet bevestigd (zie issue #993), dus geen enkel pad in deze app mag hem op <c>true</c>
    /// zetten totdat een mens dit live heeft geverifieerd.
    /// </summary>
    [Function("SportlinkMatchFieldPut")]
    public static Task<IActionResult> PutField(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sportlink/match/{wedstrijdcode}/field")] HttpRequest req,
        string wedstrijdcode,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("SportlinkMatchFieldPut"), "sportlink-veld wijzigen",
            async clubCode =>
            {
                if (!long.TryParse(wedstrijdcode, out var wedstrijdcodeValue))
                    return new BadRequestObjectResult(new { error = "wedstrijdcode moet numeriek zijn." });

                var sportlinkClient = context.InstanceServices.GetService<ISportlinkClubClient>();
                var dto = JsonConvert.DeserializeObject<VeldDto>(
                    await new StreamReader(req.Body).ReadToEndAsync());

                return await ExecuteMutationAsync(
                    req, sportlinkClient, wedstrijdcodeValue, clubCode, "UpdateMatchField",
                    SportlinkMutationSoort.Veld, dto, context,
                    publicMatchId => sportlinkClient!.UpdateFieldAsync(
                        RolNaam, publicMatchId, dto?.FieldId, dto?.FieldSize, dto?.FieldOffset, isForceUpdate: false));
            },
            requireRole: EasyAuthHelper.RequireWedstrijdzaken);

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

    /// <summary>Gedeelde stappen voor elke mutatie-actie op een bestaande wedstrijd: PublicMatchId
    /// resolven, huidige match ophalen (voor de guardrail), <see cref="SportlinkMutationGuard"/>,
    /// audit "Pending" loggen, <paramref name="mutationCall"/> uitvoeren, audit voltooien met het
    /// echte resultaat. Eén vertaalpunt voor #992/#993 en toekomstige match-mutaties — een losse
    /// kopie per endpoint zou het risico geven dat een nieuw endpoint de guard of de audit-log
    /// vergeet.</summary>
    private static async Task<IActionResult> ExecuteMutationAsync(
        HttpRequest req,
        ISportlinkClubClient? sportlinkClient,
        long wedstrijdcodeValue,
        string clubCode,
        string actie,
        SportlinkMutationSoort soort,
        object? waardeNaDto,
        FunctionContext context,
        Func<string, Task<SportlinkClubResponse<SportlinkMutationResult>>> mutationCall)
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
        var triggerdDoor = EasyAuthHelper.GetCallerName(req) ?? EasyAuthHelper.GetCallerEmail(req) ?? "onbekend";
        var auditEntry = new SportlinkMutationAuditEntry(
            clubCode, RolNaam, triggerdDoor, publicMatchId!, actie,
            WaardeVoor: JsonConvert.SerializeObject(matchResult.Data.TaskStatus),
            WaardeNa: JsonConvert.SerializeObject(waardeNaDto),
            CorrelationId: null);
        var auditId = auditService == null ? (long?)null : await auditService.LogPogingAsync(auditEntry);

        if (!guard.IsToegstaan)
        {
            if (auditId.HasValue) await auditService!.VoltooiAsync(auditId.Value, "Geblokkeerd", guard.Reden);
            return new ObjectResult(new { error = guard.Reden }) { StatusCode = 409 };
        }

        var mutationResult = await mutationCall(publicMatchId!);

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

        var violationsSamenvatting = mutationResult.Data.Violations is { Count: > 0 }
            ? string.Join(", ", mutationResult.Data.Violations)
            : null;
        if (auditId.HasValue)
            await auditService!.VoltooiAsync(auditId.Value, mutationResult.Data.IsSuccess ? "Success" : "Failure", violationsSamenvatting);

        // Altijd HTTP 200: "Sportlink heeft de mutatie inhoudelijk afgewezen" is geen transportfout
        // maar een structureel resultaat — IsSuccess/Violations dragen de uitkomst, consistent met
        // hoe BlazorAdmin/Services/AdminApiClient.cs elk non-2xx-antwoord behandelt (ruwe tekst in
        // ErrorMessage, niet gedeserialiseerd).
        return new OkObjectResult(mutationResult.Data);
    }

    /// <summary>Gedeelde stappen van beide endpoints hierboven: toggle-check, EgressGuard,
    /// wedstrijd-lookup en PublicMatchId-cache/reverse-lookup. Geen van beide aanroepers heeft de
    /// DB-connectie na afloop nog nodig (de resterende stap is telkens een HTTP-aanroep naar
    /// Sportlink), dus de connectie leeft uitsluitend binnen deze methode.</summary>
    private static async Task<(IActionResult? Fout, string? PublicMatchId)>
        BereidPublicMatchIdVoorAsync(ISportlinkClubClient? sportlinkClient, long wedstrijdcodeValue, string clubCode)
    {
        if (PostgresAppSettings.GetSetting("sportlinkExtensionEnabled") != "1")
            return (new ObjectResult(new { error = "Sportlink Web Extension staat uit." }) { StatusCode = 409 }, null);

        if (!EgressGuard.ExternalIntegrationsAllowed())
            return (new ObjectResult(new { error = "Uitgaande integraties staan hier niet toe." }) { StatusCode = 503 }, null);

        if (sportlinkClient == null)
            return (new ObjectResult(new { error = "Sportlink-client niet geconfigureerd." }) { StatusCode = 503 }, null);

        await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
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

    /// <summary>Vertaalt <see cref="SportlinkClubCallStatus"/> naar een HTTP-foutrespons — nooit de
    /// onderliggende Sportlink-foutdetails 1-op-1 doorzetten (CISO-regel). Retourneert <c>null</c>
    /// bij <c>Ok</c> (aanroeper gaat verder met de data).</summary>
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
