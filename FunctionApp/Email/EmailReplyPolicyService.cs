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
    private const string GeenAiAntwoordLabel = "Geen AI antwoord";

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
        // Review mode blijft de eerste check: hier gaat geen enkel bericht de deur uit. Nieuw is dat
        // het voorgestelde antwoord wél wordt opgebouwd en opgeslagen — anders valt er niets te
        // reviewen. Voorheen bleef AntwoordEmail leeg en kreeg de rij status 'Verwerkt', dezelfde
        // waarde als een mislukte verzending, waardoor EmailStatus.Review dode code was. (#712)
        if (reviewMode)
        {
            var reviewBesluit = ReplyPolicy.Bepaal(classificatie, plannerResponseJson);
            if (reviewBesluit.MoetVersturen)
            {
                var (_, voorgesteldeBody) = await bouwTemplateAntwoordAsync();
                await persistenceService.UpdateVoorgesteldAntwoordAsync(verwerkingId, voorgesteldeBody);
                log.LogInformation(
                    "Email {Id} review mode — voorgesteld antwoord opgeslagen ter beoordeling, niets verstuurd", verwerkingId);
            }
            else
            {
                await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.Review, null);
                log.LogInformation(
                    "Email {Id} review mode — geen antwoord voorgesteld: {Reden}", verwerkingId, reviewBesluit.Reden);
            }

            try
            {
                await graphService.EnsureMasterCategoryAsync(GeenAiAntwoordLabel, "preset0");
                await graphService.SetCategoriesAsync(email.MessageId, GeenAiAntwoordLabel);
                await graphService.MarkAsReadAsync(email.MessageId);
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

        // Vanaf hier is het antwoord de deur uit. Faalt het vastleggen, dan mag het bericht NIET
        // ongelezen blijven: de volgende poll zou de afzender een tweede antwoord sturen. Daarom
        // wordt de fout alleen gelogd en gaat het als-gelezen-markeren altijd door. (#712)
        try
        {
            await persistenceService.UpdateAntwoordVerstuurdAsync(verwerkingId, email.Afzender, antwoordBody);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Antwoord verstuurd voor verwerking {Id} maar niet vastgelegd in de database — "
                + "bericht wordt alsnog als gelezen gemarkeerd om een tweede antwoord te voorkomen", verwerkingId);
        }

        await graphService.MarkAsReadAsync(email.MessageId);

        log.LogInformation("Email {Id} volledig verwerkt, antwoord verstuurd (ontvanger niet gelogd — AVG #210)",
            verwerkingId);

        return ReplyVerwerkingUitkomst.AntwoordVerstuurd;
    }
}
