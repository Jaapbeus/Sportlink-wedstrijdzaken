using Microsoft.Extensions.Logging;
using SportlinkFunction.Planner;
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
        string? reviewRecipient,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log)
    {
        // Review mode blijft de eerste check: er gaat nooit een antwoord naar de originele
        // afzender. Het voorgestelde antwoord wordt opgebouwd en opgeslagen (#712) — zonder dat
        // valt er niets te reviewen. Daarnaast wordt hetzelfde voorstel ook gemaild naar
        // EmailReviewRecipient (#801, herstel van een regressie uit #543/2026-06-20): zonder deze
        // mail is het voorstel alleen via directe databasetoegang te lezen, omdat de Admin GUI
        // AntwoordEmail bewust nooit teruggeeft (AVG). Een mislukte reviewmail blokkeert de opslag
        // en labeling niet — het voorstel blijft dan alsnog in de database te vinden.
        return reviewMode
            ? await HandelReviewModeAsync(
                verwerkingId, email, classificatie, plannerResponseJson, reviewRecipient,
                graphService, persistenceService, bouwTemplateAntwoordAsync, sanitizeFoutMelding, log)
            : await HandelNormaalVerstuurAsync(
                verwerkingId, email, classificatie, plannerResponseJson,
                graphService, persistenceService, bouwTemplateAntwoordAsync, sanitizeFoutMelding, log);
    }

    private async Task<ReplyVerwerkingUitkomst> HandelReviewModeAsync(
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        string? reviewRecipient,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log)
    {
        var reviewBesluit = ReplyPolicy.Bepaal(classificatie, plannerResponseJson);
        if (reviewBesluit.MoetVersturen)
        {
            var (voorgesteldOnderwerp, voorgesteldeBody) = await bouwTemplateAntwoordAsync();
            await persistenceService.UpdateVoorgesteldAntwoordAsync(verwerkingId, voorgesteldeBody);
            log.LogInformation(
                "Email {Id} review mode — voorgesteld antwoord opgeslagen ter beoordeling", verwerkingId);

            if (!string.IsNullOrWhiteSpace(reviewRecipient))
            {
                try
                {
                    await graphService.SendReplyAsync(reviewRecipient, voorgesteldOnderwerp, voorgesteldeBody, email.ConversationId);
                    log.LogInformation(
                        "Email {Id} review mode — testantwoord verstuurd naar EmailReviewRecipient", verwerkingId);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex,
                        "Email {Id} review mode — testmail naar EmailReviewRecipient mislukt, voorstel blijft wel opgeslagen in de database",
                        verwerkingId);
                }
            }
            else
            {
                log.LogInformation(
                    "Email {Id} review mode — EmailReviewRecipient niet geconfigureerd, geen testmail verstuurd", verwerkingId);
            }
        }
        else
        {
            await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.Review, null);
            log.LogInformation(
                "Email {Id} review mode — geen antwoord voorgesteld: {Reden}", verwerkingId, reviewBesluit.Reden);
        }

        try
        {
            await graphService.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
            await graphService.SetCategoriesAsync(email.MessageId, EmailCategorieLabels.GeenAiAntwoord);
            await graphService.MarkAsReadAsync(email.MessageId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Graph-categorie mislukt voor verwerking {Id} in review mode", verwerkingId);
            try { await persistenceService.UpdateFoutAsync(verwerkingId, sanitizeFoutMelding(ex.Message)); }
            catch (Exception logEx) { log.LogWarning(logEx, "Kon foutstatus niet vastleggen voor verwerking {Id}", verwerkingId); }
        }

        return ReplyVerwerkingUitkomst.AfgerondZonderAntwoord;
    }

    private async Task<ReplyVerwerkingUitkomst> HandelNormaalVerstuurAsync(
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log)
    {
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

        // Verzendintentie vóór het versturen (#716). Wordt de invocatie hierna hard afgebroken
        // (functie-time-out, host-recycle, scale-in), dan is dit het enige spoor dat er misschien al
        // een antwoord de deur uit is — de volgende poll stuurt dan geen tweede antwoord meer maar legt
        // het bericht ter beoordeling neer. Mislukt het vastleggen van de intentie zelf, dan wordt er
        // niet verstuurd: zonder die grens is een dubbel antwoord mogelijk, en dat weegt zwaarder dan
        // een poging uitstellen naar de volgende poll.
        await persistenceService.MarkeerVerzendPogingAsync(verwerkingId);

        IReadOnlyList<string>? bcc = null;
        EmailBijlage? bijlage = null;
        if (classificatie.VoegKnvbPdfBijlageToe)
        {
            // #561: verzet-zonder-datum — begeleiding van ons eigen team in BCC, KNVB-kalender als
            // bijlage. Beide zijn fail-safe: ontbreekt het contact of het bestand, dan verstuurt de
            // mail gewoon zonder (nooit een crash op deze verrijking).
            try
            {
                var contact = await PlannerDataAccess.GetTeamleiderContactAsync(classificatie.TeamNaam ?? "");
                if (contact != null && !string.IsNullOrWhiteSpace(contact.Emailadres))
                {
                    bcc = new[] { contact.Emailadres };
                }
                else
                {
                    log.LogInformation("VERZET-ZONDER-DATUM - geen begeleidingscontact gevonden voor BCC, verzonden zonder BCC");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "VERZET-ZONDER-DATUM - ophalen begeleidingscontact mislukt, verzonden zonder BCC");
            }

            if (!string.IsNullOrWhiteSpace(classificatie.KnvbBijlageRegio))
            {
                var seizoen = await SystemUtilities.SeasonHelper.GetCurrentKnvbSeizoenAsync(log);
                if (!string.IsNullOrWhiteSpace(seizoen))
                    bijlage = await KnvbPdfService.GetKalenderPdfAsync(classificatie.KnvbBijlageRegio, seizoen, log);
            }
        }

        try
        {
            await graphService.SendReplyAsync(email.Afzender, onderwerp, antwoordBody, email.ConversationId, bcc, bijlage);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Graph-send mislukt voor verwerking {Id} — VerzendFout, mail blijft ongelezen", verwerkingId);
            // Het versturen is aantoonbaar mislukt, dus de intentie moet weg: anders is dit scenario —
            // waarin juist wél opnieuw geprobeerd moet worden (#712) — niet te onderscheiden van een
            // onbekende uitkomst en belandt het bericht onnodig op Review.
            try { await persistenceService.WisVerzendPogingAsync(verwerkingId); }
            catch (Exception wisEx)
            {
                log.LogWarning(wisEx,
                    "Verzendintentie kon niet gewist worden voor verwerking {Id} — een volgende poll legt dit "
                    + "bericht ter beoordeling neer in plaats van opnieuw te versturen", verwerkingId);
            }
            try { await persistenceService.UpdateFoutAsync(verwerkingId, sanitizeFoutMelding(ex.Message)); }
            catch (Exception logEx) { log.LogWarning(logEx, "Kon foutstatus niet vastleggen voor verwerking {Id}", verwerkingId); }
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
