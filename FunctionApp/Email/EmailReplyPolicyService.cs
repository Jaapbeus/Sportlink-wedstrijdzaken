using Microsoft.Extensions.Logging;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using SportlinkFunction.Planner;
using SportlinkFunction.Processing;

namespace SportlinkFunction.Email;

/// <summary>
/// SQL Server-omhulsel van de gedeelde reply-afhandeling (#1760). De beslislogica staat in
/// <see cref="EmailReplyAfhandeling"/> (<c>Planner.Shared</c>); hier blijft alleen wat per tier
/// verschilt: de opslag (<see cref="IEmailPersistenceService"/> erft <see cref="IReplyPersistentie"/>),
/// de begeleiding-BCC en KNVB-kalenderbijlage (#561; <see cref="PlannerDataAccess"/>,
/// <see cref="KnvbPdfService"/>; fail-safe) en het uitlezen van de Graph-statuscode (#1133).
/// </summary>
internal sealed class EmailReplyPolicyService
{
    internal Task<ReplyVerwerkingUitkomst> HandelReplyFlowAfAsync(
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        bool reviewMode,
        string? reviewRecipient,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log,
        TraceBuilder? trace = null,
        bool zekerheidspoortActief = true)
    {
        var haken = new ReplyTierHaken(
            async team => (await PlannerDataAccess.GetTeamleiderContactAsync(team))?.Emailadres,
            async regio =>
            {
                var seizoen = await SystemUtilities.SeasonHelper.GetCurrentKnvbSeizoenAsync(log);
                return string.IsNullOrWhiteSpace(seizoen) ? null : await KnvbPdfService.GetKalenderPdfAsync(regio, seizoen, log);
            },
            ExtraheerGraphStatusCode);

        return EmailReplyAfhandeling.HandelReplyFlowAfAsync(
            verwerkingId, new ReplyBericht(email.MessageId, email.Afzender, email.ConversationId), classificatie,
            plannerResponseJson, reviewMode, reviewRecipient, graphService, persistenceService,
            bouwTemplateAntwoordAsync, sanitizeFoutMelding, haken, log, trace, zekerheidspoortActief);
    }

    /// <summary>
    /// Haalt de HTTP-statuscode uit een Graph-foutrespons (#1133), of <c>null</c> als de exception
    /// geen respons van Graph vertegenwoordigt — bijv. een time-out of verbindingsfout vóórdat er
    /// ooit een respons was.
    /// </summary>
    private static int? ExtraheerGraphStatusCode(Exception ex)
        => ex is Microsoft.Graph.Models.ODataErrors.ODataError odataError && odataError.ResponseStatusCode > 0
            ? odataError.ResponseStatusCode
            : null;
}
