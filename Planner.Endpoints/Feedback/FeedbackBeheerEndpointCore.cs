using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Planner.Shared.Feedback;

namespace Planner.Endpoints.Feedback;

/// <summary>
/// Gedeelde orkestratie van het beheeroverzicht van feedbackmeldingen (#764): lijst, detail,
/// publiceren en inzagelog. Alleen bereikbaar voor de rol <c>admin</c> (poort in de tier-Function).
///
/// <para>
/// <b>Inzagelog.</b> Een beheerder kan hier de naam van een collega bij een melding lezen. De
/// tegenprestatie is dat elke inzage wordt vastgelegd (wie, wat, wanneer, met welk filter) in
/// <c>avg.FeedbackInzageLog</c> — voor een installatie met meerdere beheerders is dat de
/// verantwoording richting betrokkenen. De zoektekst zelf komt niet in het log (een zoekterm kan
/// zelf een naam zijn); alleen dát er gezocht is. Het log bevat nooit de inhoud van een melding.
/// </para>
/// <para>
/// Geen export (CSV/Excel): dat zou een ongecontroleerde kopie buiten bewaartermijn en
/// inzagelog om maken.
/// </para>
/// </summary>
public static class FeedbackBeheerEndpointCore
{
    // ── Lijst ──────────────────────────────────────────────────────────────────

    /// <summary>Leest de filters uit de querystring van <c>GET /api/beheer/feedback</c>.</summary>
    public static Task<IActionResult> LijstAsync(string clubCode, FeedbackAanroeper wie, IFeedbackStore store, IQueryCollection query) =>
        LijstAsync(clubCode, wie, store, query["type"], query["status"], query["vanaf"], query["tot"], query["q"], query["limit"], query["offset"]);

    public static async Task<IActionResult> LijstAsync(
        string clubCode, FeedbackAanroeper wie, IFeedbackStore store,
        string? type, string? status, string? vanaf, string? tot, string? zoek, string? limit, string? offset)
    {
        if (!string.IsNullOrWhiteSpace(type) && FeedbackCore.IsOngeldigType(type))
            return new BadRequestObjectResult(new { error = "Ongeldig type." });
        if (!string.IsNullOrWhiteSpace(status) && !FeedbackStatusWaarden.Alle.Contains(status))
            return new BadRequestObjectResult(new { error = "Ongeldige status." });
        if (!TryDatum(vanaf, out var vanafUtc, eindeVanDag: false) || !TryDatum(tot, out var totUtc, eindeVanDag: true))
            return new BadRequestObjectResult(new { error = "Ongeldige datum (verwacht jjjj-mm-dd)." });

        var filter = new FeedbackFilter(
            string.IsNullOrWhiteSpace(type) ? null : type,
            string.IsNullOrWhiteSpace(status) ? null : status,
            vanafUtc, totUtc,
            string.IsNullOrWhiteSpace(zoek) ? null : zoek.Trim()[..Math.Min(zoek.Trim().Length, 100)],
            Math.Clamp(ParseInt(limit, FeedbackEndpointCore.StandaardLimit), 1, FeedbackEndpointCore.MaxLimit),
            Math.Max(ParseInt(offset, 0), 0));

        var resultaat = await store.LijstAsync(clubCode, filter);

        await store.LogInzageAsync(new FeedbackInzageNieuw(clubCode, wie.ObjectId, wie.Naam, "lijst", null, BeschrijfFilter(filter)));

        return new OkObjectResult(new
        {
            totaal = resultaat.Totaal,
            limit = filter.Limit,
            offset = filter.Offset,
            items = resultaat.Items.Select(Naarlijstitem)
        });
    }

    /// <summary>Het filter zoals het in het inzagelog komt: welke velden, nooit de zoektekst.</summary>
    internal static string BeschrijfFilter(FeedbackFilter f)
    {
        var delen = new List<string>();
        if (f.Type is not null) delen.Add($"type={f.Type}");
        if (f.Status is not null) delen.Add($"status={f.Status}");
        if (f.VanafUtc is { } v) delen.Add($"vanaf={v:yyyy-MM-dd}");
        if (f.TotUtc is { } t) delen.Add($"tot={t:yyyy-MM-dd}");
        if (f.Zoek is not null) delen.Add("zoekterm=ja");
        return delen.Count == 0 ? "geen filter" : string.Join(", ", delen);
    }

    // ── Detail ─────────────────────────────────────────────────────────────────

    public static async Task<IActionResult> DetailAsync(string clubCode, FeedbackAanroeper wie, IFeedbackStore store, Guid id)
    {
        var detail = await store.GetDetailAsync(clubCode, id);
        if (detail is null) return new NotFoundObjectResult(new { error = "Melding niet gevonden." });

        await store.LogInzageAsync(new FeedbackInzageNieuw(clubCode, wie.ObjectId, wie.Naam, "detail", id, null));

        return new OkObjectResult(new
        {
            feedbackId = detail.Samenvatting.FeedbackId,
            aangemaaktUtc = DateTime.SpecifyKind(detail.Samenvatting.AangemaaktUtc, DateTimeKind.Utc),
            type = detail.Samenvatting.Type,
            onderwerp = detail.Samenvatting.Onderwerp,
            beschrijving = detail.Beschrijving,
            vragenAntwoorden = detail.VragenAntwoordenJson is null
                ? null
                : Newtonsoft.Json.JsonConvert.DeserializeObject<List<VraagAntwoord>>(detail.VragenAntwoordenJson)?
                    .Select(qa => new { vraag = qa.Vraag, antwoord = qa.Antwoord }),
            issueBody = detail.IssueBody,
            melderNaam = detail.Samenvatting.MelderNaam,
            isGeanonimiseerd = detail.Samenvatting.IsGeanonimiseerd,
            melderRol = detail.Samenvatting.MelderRol,
            pagina = detail.Samenvatting.Pagina,
            appVersie = detail.AppVersie,
            issueNummer = detail.Samenvatting.IssueNummer,
            issueUrl = detail.Samenvatting.IssueUrl,
            status = detail.Samenvatting.Status,
            meldingsnummer = FeedbackEndpointCore.Meldingsnummer(id),
            telemetrie = detail.Telemetrie.Select(t => new { bron = t.Bron, tekst = t.Payload })
        });
    }

    // ── Publiceren ─────────────────────────────────────────────────────────────

    public static async Task<IActionResult> PubliceerAsync(
        string clubCode, FeedbackAanroeper wie, IFeedbackStore store, Guid id,
        Func<string, string, string[], Task<(int nummer, string url)>>? maakGitHubIssueAsync, ILogger log)
    {
        if (maakGitHubIssueAsync is null)
            return new ObjectResult(new { error = "GitHub-integratie niet geconfigureerd. Neem contact op met de beheerder." }) { StatusCode = 503 };

        var detail = await store.GetDetailAsync(clubCode, id);
        if (detail is null) return new NotFoundObjectResult(new { error = "Melding niet gevonden." });
        if (detail.Samenvatting.Status == FeedbackStatusWaarden.Gepubliceerd)
            return new ConflictObjectResult(new { error = "Deze melding is al gepubliceerd.", issueNummer = detail.Samenvatting.IssueNummer, issueUrl = detail.Samenvatting.IssueUrl });

        // Defense in depth: de tekst is bij indienen al door de PII-gates gegaan, maar de beheerder
        // publiceert later — de gate draait daarom opnieuw vlak vóór de GitHub-write.
        if (FeedbackCore.BevatPii(detail.Samenvatting.Onderwerp) || FeedbackCore.BevatPii(detail.IssueBody))
            return new ObjectResult(new { error = "De tekst bevat mogelijk persoonsgegevens en is niet gepubliceerd." }) { StatusCode = 422 };

        if (!await store.ClaimPublicatieAsync(clubCode, id))
            return new ConflictObjectResult(new { error = "Deze melding wordt al gepubliceerd." });

        await store.LogInzageAsync(new FeedbackInzageNieuw(clubCode, wie.ObjectId, wie.Naam, "publiceer", id, null));

        try
        {
            var (nummer, url) = await maakGitHubIssueAsync(
                detail.Samenvatting.Onderwerp, detail.IssueBody, FeedbackCore.KiesLabels(detail.Samenvatting.Type));
            await store.ZetGepubliceerdAsync(clubCode, id, nummer, url);
            return new OkObjectResult(new { feedbackId = id, status = FeedbackStatusWaarden.Gepubliceerd, issueNummer = nummer, issueUrl = url });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Feedback publiceren vanuit overzicht mislukt [feedbackId={Id}]", id);
            await store.ZetPublicatieMisluktAsync(clubCode, id);
            return new ObjectResult(new { error = "Publiceren op GitHub is mislukt. Probeer het later opnieuw." }) { StatusCode = 502 };
        }
    }

    // ── Inzagelog ──────────────────────────────────────────────────────────────

    public static Task<IActionResult> InzagelogAsync(string clubCode, IFeedbackStore store, IQueryCollection query) =>
        InzagelogAsync(clubCode, store, query["limit"], query["offset"]);

    public static async Task<IActionResult> InzagelogAsync(string clubCode, IFeedbackStore store, string? limit, string? offset)
    {
        var l = Math.Clamp(ParseInt(limit, FeedbackEndpointCore.StandaardLimit), 1, FeedbackEndpointCore.MaxLimit);
        var o = Math.Max(ParseInt(offset, 0), 0);
        var lijst = await store.LijstInzageAsync(clubCode, l, o);
        return new OkObjectResult(new
        {
            totaal = lijst.Totaal,
            limit = l,
            offset = o,
            items = lijst.Items.Select(i => new
            {
                tijdstipUtc = DateTime.SpecifyKind(i.TijdstipUtc, DateTimeKind.Utc),
                inzienDoor = i.InzienDoorNaam,
                actie = i.Actie,
                feedbackId = i.FeedbackId,
                filter = i.Filter
            })
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static object Naarlijstitem(FeedbackSamenvatting s) => new
    {
        feedbackId = s.FeedbackId,
        aangemaaktUtc = DateTime.SpecifyKind(s.AangemaaktUtc, DateTimeKind.Utc),
        type = s.Type,
        onderwerp = s.Onderwerp,
        melderNaam = s.MelderNaam,
        isGeanonimiseerd = s.IsGeanonimiseerd,
        melderRol = s.MelderRol,
        pagina = s.Pagina,
        issueNummer = s.IssueNummer,
        issueUrl = s.IssueUrl,
        status = s.Status,
        meldingsnummer = FeedbackEndpointCore.Meldingsnummer(s.FeedbackId)
    };

    private static int ParseInt(string? waarde, int standaard) =>
        int.TryParse(waarde, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : standaard;

    private static bool TryDatum(string? waarde, out DateTime? utc, bool eindeVanDag)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(waarde)) return true;
        if (!DateTime.TryParseExact(waarde, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return false;
        utc = eindeVanDag ? d.AddDays(1) : d;   // tot-grens is exclusief: tot en met die dag
        return true;
    }
}
