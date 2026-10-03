using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Planner.Endpoints.Feedback;
using Planner.Shared.Feedback;
using SportlinkFunction.Feedback;

namespace SportlinkFunction.Admin;

/// <summary>
/// Beheeroverzicht van feedbackmeldingen (#764, #1478) — SQL Server-tier-tegenhanger van <c>FunctionApp.Postgres/Admin/AdminFeedbackFunction.cs</c>. Alleen rol <c>admin</c>
/// (<see cref="AdminEndpoint.ExecuteAsync"/>). Alle orkestratie (filters, inzagelog, publicatie)
/// staat in <c>Planner.Endpoints/Feedback/FeedbackBeheerEndpointCore</c>; dit bestand bevat alleen de
/// route-registratie en het doorgeven van de tier-eigen opslag.
/// </summary>
public static class AdminFeedbackFunction
{
    private static FeedbackAanroeper Aanroeper(HttpRequest req) => new(
        EasyAuthHelper.GetCallerObjectId(req), EasyAuthHelper.GetCallerName(req), EasyAuthHelper.IsAdmin(req));

    private static IFeedbackStore Opslag() => new SqlFeedbackStore(SystemUtilities.DatabaseConfig.ConnectionString);

    [Function("AdminFeedbackLijst")]
    public static Task<IActionResult> Lijst(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/feedback")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminFeedbackLijst"), "feedbackoverzicht ophalen",
            clubCode => FeedbackBeheerEndpointCore.LijstAsync(clubCode, Aanroeper(req), Opslag(), req.Query));

    [Function("AdminFeedbackInzagelog")]
    public static Task<IActionResult> Inzagelog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/feedback/inzagelog")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminFeedbackInzagelog"), "feedback-inzagelog ophalen",
            clubCode => FeedbackBeheerEndpointCore.InzagelogAsync(clubCode, Opslag(), req.Query));

    [Function("AdminFeedbackDetail")]
    public static Task<IActionResult> Detail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/feedback/{id:guid}")] HttpRequest req,
        Guid id,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminFeedbackDetail"), "feedbackmelding ophalen",
            clubCode => FeedbackBeheerEndpointCore.DetailAsync(clubCode, Aanroeper(req), Opslag(), id));

    [Function("AdminFeedbackPubliceer")]
    public static Task<IActionResult> Publiceer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/feedback/{id:guid}/publiceer")] HttpRequest req,
        Guid id,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminFeedbackPubliceer");
        return AdminEndpoint.ExecuteAsync(req, log, "feedbackmelding publiceren",
            clubCode => FeedbackBeheerEndpointCore.PubliceerAsync(clubCode, Aanroeper(req), Opslag(), id,
                FeedbackEndpointCore.MaakGitHubIssueDelegate(Infrastructure.EgressGuard.ExternalIntegrationsAllowed(), log), log));
    }
}
