using Microsoft.Extensions.Logging;
using SportlinkFunction.Processing;

namespace SportlinkFunction.Email;

internal enum ReplyVerwerkingUitkomst
{
    AfgerondZonderAntwoord,
    AntwoordVerstuurd,
    VerzendFout
}

internal sealed class EmailReplyPolicyService
{
    private const string HandmatigePlanningLabel = "Handmatige planning";

    internal async Task<ReplyVerwerkingUitkomst> HandelReplyFlowAfAsync(
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        bool reviewMode,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log)
    {
        if (reviewMode)
        {
            try
            {
                await graphService.EnsureMasterCategoryAsync("Geen AI antwoord", "preset0");
                await graphService.SetCategoriesAsync(email.MessageId, "Geen AI antwoord");
                await graphService.MarkAsReadAsync(email.MessageId);
                log.LogInformation("Email {Id} review mode — Geen AI antwoord label gezet, geen reply verstuurd", verwerkingId);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Graph-categorie mislukt voor verwerking {Id} in review mode", verwerkingId);
                try { await persistenceService.UpdateFoutAsync(email.MessageId, sanitizeFoutMelding(ex.Message)); } catch { }
            }

            return ReplyVerwerkingUitkomst.AfgerondZonderAntwoord;
        }

        var replyBesluit = ReplyPolicy.Bepaal(classificatie, plannerResponseJson);
        if (!replyBesluit.MoetVersturen)
        {
            await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.GeenAntwoordNodig, null);
            try
            {
                await graphService.EnsureMasterCategoryAsync(HandmatigePlanningLabel, "preset5");
                await graphService.SetCategoriesAsync(email.MessageId, HandmatigePlanningLabel);
                await graphService.MarkAsReadAsync(email.MessageId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Outlook-labeling mislukt voor verwerking {Id} — verwerking zelf is afgerond", verwerkingId);
            }

            log.LogInformation("Email {Id} verwerkt zonder automatisch antwoord: {Reden}",
                verwerkingId, replyBesluit.Reden);
            return ReplyVerwerkingUitkomst.AfgerondZonderAntwoord;
        }

        var (onderwerp, antwoordBody) = await bouwTemplateAntwoordAsync();

        try
        {
            await graphService.SendReplyAsync(email.Afzender, onderwerp, antwoordBody, email.ConversationId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Graph-send mislukt voor verwerking {Id} — VerzendFout, mail blijft ongelezen", verwerkingId);
            try { await persistenceService.UpdateFoutAsync(email.MessageId, sanitizeFoutMelding(ex.Message)); } catch { }
            return ReplyVerwerkingUitkomst.VerzendFout;
        }

        await persistenceService.UpdateAntwoordVerstuurdAsync(verwerkingId, email.Afzender, antwoordBody);
        await graphService.MarkAsReadAsync(email.MessageId);

        log.LogInformation("Email {Id} volledig verwerkt, antwoord verstuurd (ontvanger niet gelogd — AVG #210)",
            verwerkingId);

        return ReplyVerwerkingUitkomst.AntwoordVerstuurd;
    }
}
