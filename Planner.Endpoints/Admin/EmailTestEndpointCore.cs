using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;

namespace Planner.Endpoints.Admin;

/// <summary>Body van <c>POST /api/test/email</c>.</summary>
public sealed class TestEmailRequest
{
    public string? Onderwerp { get; set; }
    public string? Afzender { get; set; }
    public string? AfzenderNaam { get; set; }
    public string? Body { get; set; }
}

/// <summary>
/// De beleidsgegevens die het eindoordeel van de tester bepalen (#1583): de actuele clubinstelling
/// <c>ZekerheidspoortActief</c>, de uitkomst van het reply-beleid (dat per tier een eigen type heeft) en de algemene
/// reviewmodus (<c>EmailReviewMode</c>, gelezen met <see cref="Planner.Shared.Email.EmailReviewModus"/> zoals de processor).
/// </summary>
public sealed record TesterBeleid(bool PoortActief, bool ReplyMoetVersturen, string? ReplyReden, bool ReviewModus);

/// <summary>
/// Tier-onafhankelijke aansluiting van de e-mailtester (dry-run): rate limiting (max 10 per minuut), het lezen
/// en valideren van de request, de foutvertaling en de vorm van het antwoord. De classificatie en de pipeline
/// blijven per tier (eigen databasetoegang en <c>BerichtPipeline</c>). Samengebracht in #1568 deel C, toen de
/// tester dezelfde stappen als productie ging volgen en beide tiers daarvoor opnieuw dezelfde regels nodig hadden.
/// </summary>
public static class EmailTestEndpointCore
{
    public const int MaxCallsPerMinute = 10;
    private static readonly ConcurrentQueue<DateTime> Calls = new();
    private static readonly object Lock = new();

    /// <summary>Geeft een 429 als de limiet is bereikt, anders <c>null</c> (en telt de aanroep mee).</summary>
    public static IActionResult? ControleerLimiet()
        => TryAcquireSlot()
            ? null
            : new ObjectResult(new { error = $"Rate limit overschreden: max {MaxCallsPerMinute}/min" }) { StatusCode = 429 };

    private static bool TryAcquireSlot()
    {
        lock (Lock)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            while (Calls.TryPeek(out var first) && first < cutoff)
                Calls.TryDequeue(out _);
            if (Calls.Count >= MaxCallsPerMinute) return false;
            Calls.Enqueue(DateTime.UtcNow);
            return true;
        }
    }

    /// <summary>Leest de request; een lege body geeft een 400 in <c>Fout</c>.</summary>
    public static async Task<(TestEmailRequest? Dto, IActionResult? Fout)> LeesRequestAsync(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body);
        var dto = JsonConvert.DeserializeObject<TestEmailRequest>(await reader.ReadToEndAsync());
        return dto == null || string.IsNullOrWhiteSpace(dto.Body)
            ? (null, new BadRequestObjectResult(new { error = "Onderwerp/afzender/body verplicht" }))
            : (dto, null);
    }

    /// <summary>
    /// Lokaal is het exceptietype in de melding het enige diagnosemiddel van de e-mailtester; in productie blijft de
    /// tekst generiek (#1350).
    /// </summary>
    public static IActionResult Fout(Exception ex, ILogger log)
    {
        log.LogError(ex, "Fout bij dry-run email");
        var isLocal = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
        var errorMsg = isLocal ? $"Dry-run mislukt: {ex.GetType().Name}: {ex.Message}" : "Dry-run mislukt";
        return new ObjectResult(new { error = errorMsg }) { StatusCode = 500 };
    }

    /// <summary>
    /// Het antwoord van de tester. <c>leersuggestie</c> is de gesaneerde samenvatting als voorzet voor "Verzoektype
    /// corrigeren" (#1568 deel C); de beheerder redigeert hem en de server saneert hem bij het opslaan nogmaals.
    /// <para>
    /// #1583: het <c>eindoordeel</c> weegt de actuele instelling van de zekerheidspoort mee en volgt de volgorde van de
    /// productieverwerking: eerst het reply-beleid, daarna de poort. Alleen als het reply-beleid een antwoord toestaat
    /// komt de poortstap in de trace (zoals in productie). De tester blijft een dry-run: er wordt niets opgeslagen.
    /// </para>
    /// </summary>
    public static IActionResult Antwoord(
        object classificatie, string verzoekType, string samenvatting, string plannerResponseJson,
        TraceBuilder trace, TesterBeleid beleid, string voorbeeldOnderwerp, string voorbeeldBody)
    {
        var (poortActief, replyMoetVersturen, replyReden, reviewModus) = beleid;
        // Zoals in productie: bij reviewmodus komt het antwoord nooit bij de poort, dus geen poortstap in de trace.
        if (replyMoetVersturen && !reviewModus) ZekerheidsPoort.Bepaal(poortActief, trace);
        var bouw = trace.Bouw();
        var eindoordeel = TesterEindoordeel.Bepaal(reviewModus, replyMoetVersturen, replyReden, poortActief, bouw.Oordeel.IsZeker);

        return new OkObjectResult(new
        {
            dryRun = true,
            opmerking = "Dit verstuurt niets en slaat niets op",
            classificatie,
            plannerResponse = JsonDocument.Parse(plannerResponseJson).RootElement,
            trace = bouw.ToJsonElement(),
            eindoordeel = new
            {
                uitkomst = eindoordeel.Uitkomst.ToString(),
                titel = eindoordeel.Titel,
                toelichting = eindoordeel.Toelichting,
                waarschuwing = eindoordeel.Waarschuwing,
                conceptLabel = eindoordeel.ConceptLabel,
                zekerheidspoortActief = poortActief,
                reviewModusActief = reviewModus
            },
            leersuggestie = new
            {
                verzoekType,
                samenvatting = TraceBuilder.Saneer(samenvatting, LeermomentInvoer.MaxSamenvattingLengte)
            },
            voorbeeldAntwoord = new { onderwerp = voorbeeldOnderwerp, body = voorbeeldBody }
        });
    }
}
