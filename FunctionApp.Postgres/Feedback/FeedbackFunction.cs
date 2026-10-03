using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Endpoints.Feedback;
using Planner.Shared.Feedback;

namespace FunctionApp.Postgres.Feedback;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Feedback/FeedbackFunction.cs</c> (#966). Dunne
/// HTTP-entrypoint: de eigenlijke requestvalidatie, PII-gates, AI-promptopbouw en
/// GitHub-issue-payload zijn provider-onafhankelijk en staan sinds #1130 in
/// <see cref="Planner.Shared.Feedback.FeedbackCore"/> — gedeeld met de SQL Server-tier, waarvan dit
/// bestand vóór die verhuizing een vrijwel woordelijke kopie was. Dit bestand behoudt alleen wat per
/// tier verschilt: de HTTP-trigger zelf, de EasyAuth-poort en de rate limiter/GitHub-configuratie uit
/// env vars.
///
/// POST /api/feedback/validate
///   Valideert of de gebruikersbeschrijving voldoende informatie bevat.
///   Open voor elke ingelogde rol (admin én user, #764). Per gebruiker begrensd (AI-aanroepen).
///
/// POST /api/feedback/preview
///   Stelt de exacte titel + body samen die gepubliceerd zou worden, en geeft die terug zonder
///   iets aan te maken (#1205). Per gebruiker begrensd (AI-aanroepen); er wordt niets gepubliceerd.
///
/// POST /api/feedback/submit
///   Structureert de feedback met AI en maakt een GitHub Issue aan. Stuurt de client de in het
///   voorbeeld getoonde velden mee (<c>bevestiging</c>), dan wordt exact díe tekst gepubliceerd
///   zonder nieuwe AI-aanroep.
///   Open voor elke ingelogde rol (#764). Bewaart de melding in <c>avg.Feedback</c> (melder = Entra
///   object-ID + naam-momentopname, nooit in het publieke issue). Beheerder: direct naar GitHub;
///   gewone gebruiker: wacht op publicatie door een beheerder. Limiet per gebruiker: 3 per 10 minuten.
/// </summary>
public static class FeedbackFunction
{
    // #764: de feedback-endpoints zijn open voor elke ingelogde rol (admin én user) via
    // AdminEndpoint.ExecuteAuthenticatedAsync — dezelfde poort als de andere "voor alle gebruikers"-
    // endpoints (#1330), met databasewacht (de melding wordt bewaard). Een melding van een gewone
    // gebruiker gaat nooit rechtstreeks naar GitHub; zie FeedbackEndpointCore voor het beleid.

    /// <summary>De aanroeper uitsluitend uit het Easy Auth-principal — nooit uit de requestbody.</summary>
    private static FeedbackAanroeper Aanroeper(HttpRequest req) => new(
        Admin.EasyAuthHelper.GetCallerObjectId(req),
        Admin.EasyAuthHelper.GetCallerName(req),
        Admin.EasyAuthHelper.IsAdmin(req));

    // ── Validate ──────────────────────────────────────────────────────────────

    [Function("FeedbackValidate")]
    public static Task<IActionResult> Validate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback/validate")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("FeedbackValidate");
        return Admin.AdminEndpoint.ExecuteAuthenticatedAsync(req, log, "feedback valideren",
            async _ =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var dto = JsonConvert.DeserializeObject<FeedbackRequest>(body);
                if (FeedbackEndpointCore.ControleerEnSaneer(dto, Aanroeper(req), "Type en beschrijving zijn verplicht.") is { } afwijzing)
                    return afwijzing;

                var chatClient = context.InstanceServices.GetService<IChatClient>()
                    ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");
                return await ValidateCoreAsync(dto!, chatClient, log);
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
        // Per gebruiker begrensd (de AI-aanroep kost geld), maar publiceert niets.
        return Admin.AdminEndpoint.ExecuteAuthenticatedAsync(req, log, "feedback-voorbeeld samenstellen",
            async _ =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var dto = JsonConvert.DeserializeObject<FeedbackRequest>(body);
                if (FeedbackEndpointCore.ControleerEnSaneer(dto, Aanroeper(req), "Beschrijving is verplicht.") is { } afwijzing)
                    return afwijzing;

                var chatClient = context.InstanceServices.GetService<IChatClient>()
                    ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");

                return await PreviewCoreAsync(dto!, chatClient, log);
            });
    }

    /// <summary>
    /// Testbare kern van <see cref="Preview"/>, los van <see cref="HttpRequest"/>/<see cref="FunctionContext"/>
    /// (#1205). Vertaalt het provider-onafhankelijke resultaat van <see cref="FeedbackCore.VoorbeeldAsync"/>
    /// naar de tier-specifieke <see cref="IActionResult"/> — met exact dezelfde statuscodes als
    /// <see cref="FeedbackEndpointCore.SubmitAsync"/>, zodat een voorbeeld nooit doorkomt waar een publicatie zou afketsen.
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
        return Admin.AdminEndpoint.ExecuteAuthenticatedAsync(req, log, "feedback indienen",
            async clubCode =>
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var dto = JsonConvert.DeserializeObject<FeedbackRequest>(body);
                var wie = Aanroeper(req);
                if (FeedbackEndpointCore.ControleerEnSaneer(dto, wie, "Beschrijving is verplicht.") is { } afwijzing)
                    return afwijzing;

                var chatClient = context.InstanceServices.GetService<IChatClient>()
                    ?? throw new InvalidOperationException("IChatClient niet geconfigureerd — controleer OpenAiApiKey env var");

                return await FeedbackEndpointCore.SubmitAsync(
                    dto!, wie, clubCode, chatClient, new PostgresFeedbackStore(PostgresDatabaseConfig.ConnectionString),
                    FeedbackEndpointCore.MaakGitHubIssueDelegate(Infrastructure.EgressGuard.ExternalIntegrationsAllowed(), log), log);
            });
    }
}
