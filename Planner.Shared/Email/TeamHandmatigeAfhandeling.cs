using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>Terminal manual outcome: no templates, contacts, send intent or outgoing message.</summary>
public static class TeamHandmatigeAfhandeling
{
    public static string PlannerResponse(BerichtClassificatie classification)
    {
        classification.TeamNaamOnbevestigd = true;
        return Newtonsoft.Json.JsonConvert.SerializeObject(new { handmatigeTeamBeoordeling = true, status = "Handmatige teambeoordeling nodig" });
    }

    public static Task<ReplyVerwerkingUitkomst> HandelOfVervolgAsync(
        BerichtClassificatie classification, int verwerkingId, string messageId,
        IReplyPersistentie opslag, IReplyGraph graph, Func<Task<ReplyVerwerkingUitkomst>> vervolg, ILogger log)
        => classification.TeamHandmatigBeoordelen
            ? HandelAsync(verwerkingId, messageId, opslag, graph, log)
            : vervolg();

    public static async Task<ReplyVerwerkingUitkomst> HandelAsync(
        int verwerkingId, string messageId, IReplyPersistentie opslag, IReplyGraph graph, ILogger? log = null)
    {
        await opslag.UpdateStatusAsync(verwerkingId, EmailStatus.Review, null);
        try
        {
            await graph.EnsureMasterCategoryAsync(EmailCategorieLabels.HandmatigePlanning, EmailCategorieLabels.HandmatigePlanningKleur);
            await graph.SetCategoriesAsync(messageId, EmailCategorieLabels.HandmatigePlanning);
            await graph.MarkAsReadAsync(messageId);
        }
        catch (Exception ex)
        {
            log?.LogWarning("TEAMRESOLUTIE - handmatige beoordeling opgeslagen; mailboxlabel mislukt ({FoutType})", ex.GetType().Name);
        }
        return ReplyVerwerkingUitkomst.AfgerondZonderAntwoord;
    }
}
