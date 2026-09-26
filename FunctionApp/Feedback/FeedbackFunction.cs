using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared.Feedback;

namespace SportlinkFunction.Feedback;

/// <summary>
/// Feedback widget API — issue #129. Dunne HTTP-entrypoint: de eigenlijke requestvalidatie,
/// PII-gates, AI-promptopbouw en GitHub-issue-payload zijn provider-onafhankelijk en staan sinds
/// #1130 in <see cref="Planner.Shared.Feedback.FeedbackCore"/> — gedeeld met de Postgres-tier
/// (<c>FunctionApp.Postgres/Feedback/FeedbackFunction.cs</c>), die vóór die verhuizing een vrijwel
/// woordelijke kopie van dit bestand was. Dit bestand behoudt alleen wat per tier verschilt: de
/// HTTP-trigger zelf, de EasyAuth-poort en de rate limiter/GitHub-configuratie uit env vars.
///
/// POST /api/feedback/validate
///   Valideert of de gebruikersbeschrijving voldoende informatie bevat.
///   Geeft gerichte aanvulvragen terug als er gaten zijn.
///   Geen rate limiting — validatie is goedkoop en gebruiksvriendelijk.
///
/// POST /api/feedback/preview
///   Stelt de exacte titel + body samen die gepubliceerd zou worden, en geeft die terug zonder
///   iets aan te maken (#1205). Geen rate limiting — er wordt niets gepubliceerd.
///
/// POST /api/feedback/submit
///   Structureert de feedback met GPT-4o-mini en maakt een GitHub Issue aan. Stuurt de client de
///   in het voorbeeld getoonde velden mee (<c>bevestiging</c>), dan wordt exact díe tekst
///   gepubliceerd zonder nieuwe AI-aanroep.
///   Rate limiting: max 5 per 10 minuten (globaal).
/// </summary>
public static class FeedbackFunction
{
    // ── Validate ──────────────────────────────────────────────────────────────

    // #1350: via AdminEndpoint.ExecuteZonderDatabaseAsync — dezelfde poort als elk ander
    // admin-endpoint, maar zonder databasewacht: de feedback-widget moet juist blijven werken als
    // de database onbereikbaar is, want dat is een van de dingen die een beheerder wil melden.
    [Function("FeedbackValidate")]
    public static Task<IActionResult> Validate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback/validate")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("FeedbackValidate");
        return SportlinkFunction.Admin.AdminEndpoint.ExecuteZonderDatabaseAsync(req, log, "feedback valideren",
            async () =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var dto = JsonConvert.DeserializeObject<FeedbackRequest>(body);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Beschrijving))
                    return new BadRequestObjectResult(new { error = "Type en beschrijving zijn verplicht." });

                var chatClient = context.InstanceServices.GetService<IChatClient>()
                    ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");
                return await ValidateCoreAsync(dto, chatClient, log);
            });
    }

    /// <summary>
    /// Testbare kern van <see cref="Validate"/>, los van <see cref="HttpRequest"/>/<see cref="FunctionContext"/>
    /// zodat regressietests een <see cref="IChatClient"/>-fake kunnen injecteren (#1006). Vertaalt
    /// het provider-onafhankelijke resultaat van <see cref="FeedbackCore.ValidateAsync"/> naar de
    /// tier-specifieke <see cref="IActionResult"/>.
    /// </summary>
    internal static async Task<IActionResult> ValidateCoreAsync(FeedbackRequest dto, IChatClient chatClient, ILogger log)
    {
        var result = await FeedbackCore.ValidateAsync(dto, chatClient, log);
        return result.Status switch
        {
            FeedbackStatus.OngeldigType => new BadRequestObjectResult(new { error = result.Foutmelding }),
            FeedbackStatus.PiiGedetecteerd => new ObjectResult(new { error = result.Foutmelding }) { StatusCode = 422 },
            _ => new OkObjectResult(new { volledig = result.Volledig, vragen = result.Vragen })
        };
    }

    // ── Voorbeeld vóór publicatie (#1205) ──────────────────────────────────────

    [Function("FeedbackPreview")]
    public static Task<IActionResult> Preview(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback/preview")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("FeedbackPreview");
        // Bewust geen rate limiting, net als bij validate: een voorbeeld publiceert niets, en het is
        // juist de stap die de beheerder moet zetten vóór hij iets openbaar maakt. De limiter blijft
        // op submit staan — dáár gebeurt de GitHub-write.
        return SportlinkFunction.Admin.AdminEndpoint.ExecuteZonderDatabaseAsync(req, log, "feedback-voorbeeld samenstellen",
            async () =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var dto = JsonConvert.DeserializeObject<FeedbackRequest>(body);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Beschrijving))
                    return new BadRequestObjectResult(new { error = "Beschrijving is verplicht." });

                var chatClient = context.InstanceServices.GetService<IChatClient>()
                    ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");

                return await PreviewCoreAsync(dto, chatClient, log);
            });
    }

    /// <summary>
    /// Testbare kern van <see cref="Preview"/>, los van <see cref="HttpRequest"/>/<see cref="FunctionContext"/>
    /// (#1205). Vertaalt het provider-onafhankelijke resultaat van <see cref="FeedbackCore.VoorbeeldAsync"/>
    /// naar de tier-specifieke <see cref="IActionResult"/> — met exact dezelfde statuscodes als
    /// <see cref="SubmitCoreAsync"/>, zodat een voorbeeld nooit doorkomt waar een publicatie zou afketsen.
    /// </summary>
    internal static async Task<IActionResult> PreviewCoreAsync(FeedbackRequest dto, IChatClient chatClient, ILogger log)
    {
        var result = await FeedbackCore.VoorbeeldAsync(dto, chatClient, log);
        return result.Status switch
        {
            FeedbackStatus.OngeldigType => new BadRequestObjectResult(new { error = result.Foutmelding }),
            FeedbackStatus.PiiGedetecteerd => new ObjectResult(new { error = result.Foutmelding }) { StatusCode = 422 },
            _ => new OkObjectResult(new
            {
                titel = result.Titel,
                body = result.Body,
                samenvatting = result.Samenvatting,
                acceptatiecriteria = result.Acceptatiecriteria
            })
        };
    }

    // ── Submit ─────────────────────────────────────────────────────────────────

    [Function("FeedbackSubmit")]
    public static Task<IActionResult> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback/submit")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("FeedbackSubmit");
        return SportlinkFunction.Admin.AdminEndpoint.ExecuteZonderDatabaseAsync(req, log, "feedback indienen",
            async () =>
            {
                if (!FeedbackRateLimiter.TryAcquireSubmitSlot())
                    return new ObjectResult(new { error = $"Limiet bereikt: maximaal {FeedbackRateLimiter.MaxSubmissiesPerVenster} meldingen per 10 minuten." }) { StatusCode = 429 };

                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var dto = JsonConvert.DeserializeObject<FeedbackRequest>(body);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Beschrijving))
                    return new BadRequestObjectResult(new { error = "Beschrijving is verplicht." });

                var pat = Environment.GetEnvironmentVariable("GitHubPat");
                var owner = Environment.GetEnvironmentVariable("GitHubOwner")
                         ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER") ?? "";
                // GitHubRepo is net als GitHubOwner verplicht: een stille fallback op de upstream-repo-naam
                // geeft een fork met een andere naam een verwarrende 404 i.p.v. een configuratiefout. (#607)
                var repo = Environment.GetEnvironmentVariable("GitHubRepo");

                if (string.IsNullOrWhiteSpace(pat) || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
                {
                    log.LogWarning("GitHubPat/GitHubOwner/GitHubRepo niet volledig geconfigureerd — feedback-submit niet mogelijk");
                    return new ObjectResult(new { error = "GitHub-integratie niet geconfigureerd. Neem contact op met de beheerder." }) { StatusCode = 503 };
                }

                var chatClient = context.InstanceServices.GetService<IChatClient>()
                    ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");

                Task<(int nummer, string url)> MaakIssue(string title, string body, string[] labels) =>
                    FeedbackCore.MaakGitHubIssueAsync(pat, owner, repo, title, body, labels, log);

                return await SubmitCoreAsync(dto, chatClient, MaakIssue, log);
            });
    }

    /// <summary>
    /// Testbare kern van <see cref="Submit"/>, los van <see cref="HttpRequest"/>/<see cref="FunctionContext"/>
    /// en de echte GitHub-<see cref="HttpClient"/> zodat regressietests een <see cref="IChatClient"/>-fake en
    /// een GitHub-fake kunnen injecteren (#1006). Vertaalt het provider-onafhankelijke resultaat van
    /// <see cref="FeedbackCore.SubmitAsync"/> naar de tier-specifieke <see cref="IActionResult"/>.
    /// </summary>
    internal static async Task<IActionResult> SubmitCoreAsync(
        FeedbackRequest dto,
        IChatClient chatClient,
        Func<string, string, string[], Task<(int nummer, string url)>> maakGitHubIssueAsync,
        ILogger log)
    {
        var result = await FeedbackCore.SubmitAsync(dto, chatClient, maakGitHubIssueAsync, log);
        return result.Status switch
        {
            FeedbackStatus.OngeldigType => new BadRequestObjectResult(new { error = result.Foutmelding }),
            FeedbackStatus.PiiGedetecteerd => new ObjectResult(new { error = result.Foutmelding }) { StatusCode = 422 },
            _ => new OkObjectResult(new { issueNummer = result.IssueNummer, issueUrl = result.IssueUrl })
        };
    }
}
