using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.Feedback;

namespace Planner.Endpoints.Feedback;

/// <summary>
/// Wie de feedbackaanroep doet — uitsluitend server-side uit het Easy Auth-principal bepaald, nooit
/// uit de requestbody (een client kan zich anders voordoen als een ander of als beheerder).
/// </summary>
public sealed record FeedbackAanroeper(string? ObjectId, string? Naam, bool IsAdmin)
{
    public string Rol => IsAdmin ? "admin" : "user";

    /// <summary>Sleutel voor de limieten; zonder principal (lokaal) één gedeelde sleutel.</summary>
    public string LimietSleutel => string.IsNullOrWhiteSpace(ObjectId) ? "onbekend" : ObjectId;
}

/// <summary>
/// Gedeelde orkestratie van de feedback-endpoints (#764), tier-onafhankelijk — dezelfde vorm als
/// <c>SportlinkEndpointSupportCore</c> (#1271). De tier-Function doet alleen: HTTP-trigger,
/// autorisatiepoort (<c>AdminEndpoint</c>), body lezen, <see cref="FeedbackAanroeper"/> uit het
/// principal bouwen en de tier-eigen <see cref="IFeedbackStore"/> aanleveren.
///
/// <para>
/// <b>Publicatiebeleid (eigenaarsbesluit 2026-10-03).</b> Een melding van een beheerder wordt direct
/// naar GitHub gepubliceerd; een melding van een gewone gebruiker wordt bewaard met status
/// <see cref="FeedbackStatusWaarden.WachtOpPublicatie"/> en pas gepubliceerd nadat een beheerder in
/// het overzicht op "publiceren" klikt. De titel/body die dan naar GitHub gaan zijn precies de
/// tekst die bij het indienen is voorbereid en door de PII-gates is gekomen — de AI draait niet
/// opnieuw.
/// </para>
/// <para>
/// <b>Wat nooit in het publieke issue komt:</b> de identiteit van de melder (object-ID, naam), en de
/// technische context. Die staan uitsluitend in het <c>avg</c>-schema.
/// </para>
/// </summary>
public static class FeedbackEndpointCore
{
    public const int MaxMeldingenPerGebruikerPerVenster = 3;
    public const int MaxMeldingenPerClubPerUur = 30;
    public static readonly TimeSpan GebruikerVenster = TimeSpan.FromMinutes(10);
    public const int StandaardLimit = 50;
    public const int MaxLimit = 200;

    public const string LimietGebruikerMelding =
        "Je hebt net al een paar meldingen gestuurd. Probeer het over 10 minuten nog eens.";

    // ── GitHub-koppeling ───────────────────────────────────────────────────────

    /// <summary>
    /// Bouwt de delegate die een GitHub-issue aanmaakt, of <c>null</c> als dat niet mag of kan:
    /// <paramref name="externeIntegratiesToegestaan"/> is de uitkomst van de tier-eigen
    /// <c>EgressGuard.ExternalIntegrationsAllowed()</c> (#857) — één poort voor elke uitgaande
    /// aanroep, ook voor het publiceren vanuit het overzicht en de statuscontrole van de
    /// retentietimer. <c>GitHubRepo</c> is net als <c>GitHubOwner</c> verplicht (#607).
    /// </summary>
    public static Func<string, string, string[], Task<(int nummer, string url)>>? MaakGitHubIssueDelegate(
        bool externeIntegratiesToegestaan, ILogger log)
    {
        if (!TryLeesGitHubConfig(externeIntegratiesToegestaan, log, out var pat, out var owner, out var repo)) return null;
        return (titel, body, labels) => FeedbackCore.MaakGitHubIssueAsync(pat, owner, repo, titel, body, labels, log);
    }

    /// <summary>Tegenhanger voor de retentietimer: leest de sluitstatus van een issue. <c>null</c> als GitHub niet mag/kan.</summary>
    public static Func<int, Task<(bool gelukt, DateTime? geslotenOpUtc)>>? MaakIssueSluitingDelegate(
        bool externeIntegratiesToegestaan, ILogger log)
    {
        if (!TryLeesGitHubConfig(externeIntegratiesToegestaan, log, out var pat, out var owner, out var repo)) return null;
        return nummer => FeedbackCore.HaalIssueSluitingAsync(pat, owner, repo, nummer, log);
    }

    private static bool TryLeesGitHubConfig(bool toegestaan, ILogger log, out string pat, out string owner, out string repo)
    {
        pat = Environment.GetEnvironmentVariable("GitHubPat") ?? "";
        owner = Environment.GetEnvironmentVariable("GitHubOwner")
                ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER") ?? "";
        repo = Environment.GetEnvironmentVariable("GitHubRepo") ?? "";

        if (!toegestaan) return false;
        if (string.IsNullOrWhiteSpace(pat) || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            log.LogWarning("GitHubPat/GitHubOwner/GitHubRepo niet volledig geconfigureerd — GitHub-koppeling van feedback niet beschikbaar");
            return false;
        }
        return true;
    }

    /// <summary>
    /// De dagelijkse retentietimer van beide tiers (#764): wacht op de database, synchroniseert de
    /// issuestatus (alleen als <paramref name="externeIntegratiesToegestaan"/>, #857) en anonimiseert
    /// en wist wat verlopen is. Eén plek, zodat de tiers alleen de trigger en hun eigen opslag
    /// aanleveren.
    /// </summary>
    public static async Task VoerRetentieTimerAsync(
        Func<Task> wachtOpDatabase, IFeedbackStore store, bool externeIntegratiesToegestaan, ILogger log)
    {
        log.LogInformation("AVG-cleanup gestart: avg.Feedback");
        try
        {
            await wachtOpDatabase();
            await FeedbackRetentieCore.VoerUitAsync(store, MaakIssueSluitingDelegate(externeIntegratiesToegestaan, log), log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup Feedback mislukt");
            throw;
        }
    }

    // ── AI-dienst ontbreekt (#1487) ────────────────────────────────────────────

    public const string AiNietBeschikbaarMelding =
        "Het automatisch controleren van feedback is nu niet beschikbaar. Probeer het later opnieuw; er is niets verstuurd.";

    /// <summary>
    /// Validate, preview en submit hebben alle drie de AI-dienst nodig (titel, body en PII-gates
    /// komen uit <see cref="FeedbackCore"/>). Is <c>IChatClient</c> niet geregistreerd — lokaal door
    /// de EgressGuard (#857), of in productie zonder API-sleutel — dan is dat geen serverfout maar
    /// een tijdelijk onbeschikbare dienst: 503 met een begrijpelijke melding, op beide tiers gelijk.
    /// Geeft <c>null</c> als de dienst er is. Aanroepen vóór <see cref="ControleerEnSaneer"/>, zodat
    /// een niet-uitgevoerde aanroep ook geen AI-limietslot verbruikt.
    /// </summary>
    public static IActionResult? ControleerAiBeschikbaar(IChatClient? chatClient, ILogger log)
    {
        if (chatClient is not null) return null;
        log.LogWarning("Feedback-AI niet beschikbaar: IChatClient niet geregistreerd (EgressGuard of ontbrekende API-sleutel)");
        return new ObjectResult(new { error = AiNietBeschikbaarMelding, aiBeschikbaar = false }) { StatusCode = 503 };
    }

    // ── Validate / Preview: gedeelde invoerpoort ───────────────────────────────

    /// <summary>
    /// Eerste stap van validate, preview en submit: lege invoer → 400, per-gebruiker-AI-limiet →
    /// 429, en daarna de server-side redactie van de client-context (route zonder querystring,
    /// technische context geredigeerd, naam van de melder eruit). Muteert <paramref name="dto"/>.
    /// </summary>
    public static IActionResult? ControleerEnSaneer(FeedbackRequest? dto, FeedbackAanroeper wie, string ontbreektMelding)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Beschrijving))
            return new BadRequestObjectResult(new { error = ontbreektMelding });

        if (!FeedbackRateLimiter.TryAcquireAiSlot(wie.LimietSleutel))
            return new ObjectResult(new { error = LimietGebruikerMelding }) { StatusCode = 429 };

        SaneerInvoer(dto, wie.Naam, wie.Rol);
        return null;
    }

    public static void SaneerInvoer(FeedbackRequest dto, string? melderNaam, string rol)
    {
        if (dto.Context is { } ctx)
        {
            ctx.Pagina = FeedbackRedactie.RedigeerNaam(FeedbackRedactie.RedigeerRoute(ctx.Pagina), melderNaam);
            // De rol komt uit het principal, niet uit de client (B10 uit het plan van #764).
            ctx.Rol = rol;
        }
        dto.Telemetrie = FeedbackTelemetrieSaneerder.Saneer(dto.Telemetrie, melderNaam);
    }

    // ── Submit ─────────────────────────────────────────────────────────────────

    public static async Task<IActionResult> SubmitAsync(
        FeedbackRequest dto,
        FeedbackAanroeper wie,
        string clubCode,
        IChatClient chatClient,
        IFeedbackStore store,
        Func<string, string, string[], Task<(int nummer, string url)>>? maakGitHubIssueAsync,
        ILogger log,
        DateTime? nuUtc = null)
    {
        var nu = nuUtc ?? DateTime.UtcNow;

        if (await Limietoverschrijding(store, clubCode, wie, nu) is { } limiet) return limiet;

        var voorbereid = await FeedbackCore.BereidVoorAsync(dto, chatClient, log, nu);
        switch (voorbereid.Status)
        {
            case FeedbackStatus.OngeldigType:
                return new BadRequestObjectResult(new { error = voorbereid.Foutmelding });
            case FeedbackStatus.PiiGedetecteerd:
                return new ObjectResult(new { error = voorbereid.Foutmelding }) { StatusCode = 422 };
        }

        var feedbackId = Guid.NewGuid();
        var publiceertDirect = wie.IsAdmin && maakGitHubIssueAsync is not null;
        var bewaard = await ProbeerBewarenAsync(store, MaakRij(dto, wie, clubCode, feedbackId, voorbereid), dto.Telemetrie, log,
            moetSlagen: !publiceertDirect);

        if (!publiceertDirect)
        {
            if (wie.IsAdmin)
                log.LogWarning("Feedback bewaard maar niet gepubliceerd: GitHub-integratie niet geconfigureerd");
            return new OkObjectResult(Antwoord(feedbackId, FeedbackStatusWaarden.WachtOpPublicatie, bewaard: true, wie, null, null,
                wie.IsAdmin ? "GitHub-integratie niet geconfigureerd: de melding is bewaard en kan later via het overzicht gepubliceerd worden." : null));
        }

        return await PubliceerNaBewarenAsync(store, clubCode, feedbackId, bewaard, dto.Type, voorbereid, maakGitHubIssueAsync!, wie, log);
    }

    private static async Task<IActionResult?> Limietoverschrijding(IFeedbackStore store, string clubCode, FeedbackAanroeper wie, DateTime nu)
    {
        // Per gebruiker (sleutel = object-ID) en daarnaast een vangnet voor de hele club: één
        // doorklikkende vrijwilliger sluit zo de anderen niet uit (B4 uit het plan van #764).
        if (!string.IsNullOrWhiteSpace(wie.ObjectId)
            && await store.TelRecenteMeldingenAsync(clubCode, wie.ObjectId, nu - GebruikerVenster) >= MaxMeldingenPerGebruikerPerVenster)
            return new ObjectResult(new { error = LimietGebruikerMelding }) { StatusCode = 429 };

        if (await store.TelRecenteMeldingenAsync(clubCode, null, nu.AddHours(-1)) >= MaxMeldingenPerClubPerUur)
            return new ObjectResult(new { error = "Er zijn net veel meldingen binnengekomen. Probeer het over een uur nog eens." }) { StatusCode = 429 };

        return null;
    }

    private static FeedbackNieuw MaakRij(FeedbackRequest dto, FeedbackAanroeper wie, string clubCode, Guid id,
        FeedbackCore.FeedbackVoorbereiding voorbereid) => new(
            id, clubCode, dto.Type, voorbereid.Titel!, voorbereid.Body!, dto.Beschrijving,
            dto.VragenAntwoorden is { Count: > 0 } ? JsonConvert.SerializeObject(dto.VragenAntwoorden) : null,
            wie.ObjectId, wie.Naam is { Length: > 200 } n ? n[..200] : wie.Naam, wie.Rol,
            dto.Context?.Pagina is { } p ? (p.Length > 200 ? p[..200] : p) : null,
            dto.Context?.Versie is { } v ? (v.Length > 20 ? v[..20] : v) : null,
            FeedbackStatusWaarden.WachtOpPublicatie);

    /// <summary>
    /// Bewaren vóór publiceren. Een beheerder die direct publiceert verliest zijn melding niet op
    /// een weggevallen database (de gratis tier pauzeert): dan loopt de publicatie gewoon door en
    /// ontbreekt alleen het overzichtsrecord. Een gewone gebruiker heeft niets aan een melding die
    /// nergens staat, dus daar is het opslaan verplicht.
    /// </summary>
    private static async Task<bool> ProbeerBewarenAsync(
        IFeedbackStore store, FeedbackNieuw rij, FeedbackTelemetrie? telemetrie, ILogger log, bool moetSlagen)
    {
        var regels = (telemetrie?.NaarBronnen() ?? []).Select(b => new FeedbackTelemetrieRegel(b.Bron, b.Tekst)).ToList();
        try
        {
            await store.BewaarAsync(rij, regels);
            return true;
        }
        catch (Exception ex) when (!moetSlagen)
        {
            log.LogError(ex, "Feedback bewaren mislukt; publicatie gaat door zonder overzichtsrecord");
            return false;
        }
    }

    private static async Task<IActionResult> PubliceerNaBewarenAsync(
        IFeedbackStore store, string clubCode, Guid id, bool bewaard, string type,
        FeedbackCore.FeedbackVoorbereiding voorbereid,
        Func<string, string, string[], Task<(int nummer, string url)>> maakIssue,
        FeedbackAanroeper wie, ILogger log)
    {
        try
        {
            var (nummer, url) = await maakIssue(voorbereid.Titel!, voorbereid.Body!, FeedbackCore.KiesLabels(type));
            if (bewaard)
            {
                try { await store.ZetGepubliceerdAsync(clubCode, id, nummer, url); }
                catch (Exception ex)
                {
                    // Het issue bestaat; het FeedbackId staat niet in de issue-body, dus alleen loggen.
                    log.LogError(ex, "Issue #{Nr} aangemaakt maar overzichtsrecord bijwerken mislukt [feedbackId={Id}]", nummer, id);
                }
            }
            return new OkObjectResult(Antwoord(id, FeedbackStatusWaarden.Gepubliceerd, bewaard, wie, nummer, url, null));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Feedback publiceren mislukt [feedbackId={Id}]", id);
            if (bewaard)
                await ProbeerStatusMisluktAsync(store, clubCode, id, log);
            return new ObjectResult(new
            {
                error = "De melding is bewaard, maar publiceren op GitHub is mislukt. Probeer het later opnieuw via het feedbackoverzicht.",
                feedbackId = id,
                meldingsnummer = Meldingsnummer(id)
            }) { StatusCode = 502 };
        }
    }

    private static async Task ProbeerStatusMisluktAsync(IFeedbackStore store, string clubCode, Guid id, ILogger log)
    {
        try { await store.ZetPublicatieMisluktAsync(clubCode, id); }
        catch (Exception ex) { log.LogError(ex, "Status github-mislukt zetten mislukt [feedbackId={Id}]", id); }
    }

    /// <summary>Kort nummer dat een gebruiker kan noemen als hij er later naar vraagt.</summary>
    public static string Meldingsnummer(Guid id) => id.ToString("N")[..8].ToUpperInvariant();

    private static object Antwoord(Guid id, string status, bool bewaard, FeedbackAanroeper wie, int? nummer, string? url, string? waarschuwing) => new
    {
        feedbackId = id,
        meldingsnummer = Meldingsnummer(id),
        status,
        bewaardInOverzicht = bewaard,
        gepubliceerd = status == FeedbackStatusWaarden.Gepubliceerd,
        // De GitHub-verwijzing alleen voor een beheerder: een gewone gebruiker landt daar op een
        // Engelstalige ontwikkelaarspagina waar hij geen account heeft (UX-besluit in #764).
        issueNummer = wie.IsAdmin ? nummer ?? 0 : 0,
        issueUrl = wie.IsAdmin ? url : null,
        waarschuwing
    };
}
