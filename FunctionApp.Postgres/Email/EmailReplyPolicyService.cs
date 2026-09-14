using Microsoft.Extensions.Logging;
using Planner.Shared;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/EmailReplyPolicyService.cs</c> (#972 — port
/// van EmailProcessorFunction).
/// <para>
/// <b>Enige structurele afwijking t.o.v. het SQL Server-origineel:</b> in plaats van een
/// <c>IEmailPersistenceService</c>-interface krijgt deze klasse de Postgres-connectiestring en
/// roept ze <see cref="SqlEmailPersistenceRepository"/>'s statische methoden rechtstreeks aan —
/// zelfde stijl als <c>FunctionApp.Postgres.Processing.BerichtPipeline</c> al gebruikt. Een nieuwe
/// interface/DI-abstractie alleen om de SQL Server-tier 1-op-1 te spiegelen zou onnodige
/// architectuur toevoegen voor een hotfix.
/// </para>
/// <para>
/// <b>Tweede afwijking:</b> de KNVB-PDF-bijlage/BCC-tak (#561, "verzet zonder datum") is hier niet
/// vertaald — zie de klassekop van <c>BerichtPipeline</c> (item 3). <c>bcc</c>/<c>bijlage</c>
/// blijven daarom altijd <c>null</c>.
/// </para>
/// </summary>
internal enum ReplyVerwerkingUitkomst
{
    AfgerondZonderAntwoord,
    AntwoordVerstuurd,
    VerzendFout,

    /// <summary>
    /// De verzendpoging leverde een onbekende uitkomst op (#1133): Graph kan het bericht al hebben
    /// geaccepteerd vóór een time-out, annulering, verbindingsverlies of 5xx-fout. De verzendintentie
    /// blijft staan en het bericht is direct op <see cref="EmailStatus.Review"/> gezet — er is NIET
    /// opnieuw verstuurd.
    /// </summary>
    OnbekendeVerzendUitkomst
}

internal sealed class EmailReplyPolicyService
{
    private const string HandmatigePlanningLabel = "Handmatige planning";

    internal async Task<ReplyVerwerkingUitkomst> HandelReplyFlowAfAsync(
        string connectionString,
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        bool reviewMode,
        string? reviewRecipient,
        IEmailGraphService graphService,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log)
    {
        // Review mode blijft de eerste check: er gaat nooit een antwoord naar de originele
        // afzender. Het voorgestelde antwoord wordt opgebouwd en opgeslagen — zonder dat valt er
        // niets te reviewen. Daarnaast wordt hetzelfde voorstel ook gemaild naar
        // reviewRecipient (#801-precedent): zonder deze mail is het voorstel alleen via directe
        // databasetoegang te lezen, omdat de Admin GUI AntwoordEmail bewust nooit teruggeeft (AVG).
        // Een mislukte reviewmail blokkeert de opslag en labeling niet.
        return reviewMode
            ? await HandelReviewModeAsync(
                connectionString, verwerkingId, email, classificatie, plannerResponseJson, reviewRecipient,
                graphService, bouwTemplateAntwoordAsync, sanitizeFoutMelding, log)
            : await HandelNormaalVerstuurAsync(
                connectionString, verwerkingId, email, classificatie, plannerResponseJson,
                graphService, bouwTemplateAntwoordAsync, sanitizeFoutMelding, log);
    }

    private async Task<ReplyVerwerkingUitkomst> HandelReviewModeAsync(
        string connectionString,
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        string? reviewRecipient,
        IEmailGraphService graphService,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log)
    {
        var reviewBesluit = ReplyPolicy.Bepaal(classificatie, plannerResponseJson);
        if (reviewBesluit.MoetVersturen)
        {
            var (voorgesteldOnderwerp, voorgesteldeBody) = await bouwTemplateAntwoordAsync();
            await SqlEmailPersistenceRepository.UpdateVoorgesteldAntwoordAsync(connectionString, verwerkingId, voorgesteldeBody);
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
            await SqlEmailPersistenceRepository.UpdateStatusAsync(connectionString, verwerkingId, EmailStatus.Review, null);
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
            try { await SqlEmailPersistenceRepository.UpdateFoutAsync(connectionString, verwerkingId, sanitizeFoutMelding(ex.Message)); }
            catch (Exception logEx) { log.LogWarning(logEx, "Kon foutstatus niet vastleggen voor verwerking {Id}", verwerkingId); }
        }

        return ReplyVerwerkingUitkomst.AfgerondZonderAntwoord;
    }

    private async Task<ReplyVerwerkingUitkomst> HandelNormaalVerstuurAsync(
        string connectionString,
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        IEmailGraphService graphService,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log)
    {
        var replyBesluit = ReplyPolicy.Bepaal(classificatie, plannerResponseJson);
        if (!replyBesluit.MoetVersturen)
        {
            await SqlEmailPersistenceRepository.UpdateStatusAsync(connectionString, verwerkingId, EmailStatus.GeenAntwoordNodig, null);
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

        // Verzendintentie vóór het versturen. Wordt de invocatie hierna hard afgebroken
        // (functie-time-out, host-recycle, scale-in), dan is dit het enige spoor dat er misschien al
        // een antwoord de deur uit is — de volgende poll stuurt dan geen tweede antwoord meer maar legt
        // het bericht ter beoordeling neer. Mislukt het vastleggen van de intentie zelf, dan wordt er
        // niet verstuurd: zonder die grens is een dubbel antwoord mogelijk.
        await SqlEmailPersistenceRepository.MarkeerVerzendPogingAsync(connectionString, verwerkingId);

        // #561/#889 (item 3, zie BerichtPipeline-klassekop): VoegKnvbPdfBijlageToe wordt op deze
        // tier nooit true — de "verzet zonder datum"-KNVB-bijlage-flow is hier niet vertaald.
        // Bcc/bijlage blijven daarom altijd null; geen KnvbPdfService/PlannerDataAccess nodig.
        IReadOnlyList<string>? bcc = null;
        EmailBijlage? bijlage = null;

        try
        {
            await graphService.SendReplyAsync(email.Afzender, onderwerp, antwoordBody, email.ConversationId, bcc, bijlage);
        }
        catch (Exception ex)
        {
            // #1133: niet elke exception van SendReplyAsync bewijst dat er niets verstuurd is. Een
            // time-out, annulering, verbindingsverlies of 5xx kan optreden ná acceptatie door Graph —
            // alleen een HTTP-statuscode die een expliciete afwijzing bewijst mag de verzendintentie
            // laten wissen. Zie Planner.Shared.EmailVerzendFoutClassificatie voor de volledige
            // motivatie en de losstaande, pure tests.
            var graphStatusCode = ExtraheerGraphStatusCode(ex);
            var uitkomst = EmailVerzendFoutClassificatie.Classificeer(ex, graphStatusCode);

            if (uitkomst == EmailVerzendUitkomst.OnbekendeUitkomst)
            {
                log.LogError(ex,
                    "Graph-send onbekende uitkomst (status {StatusCode}) voor verwerking {Id} — Graph kan het "
                    + "bericht al hebben geaccepteerd. Verzendintentie blijft staan, NIET opnieuw versturen — "
                    + "bericht direct op Review gezet (#1133)", graphStatusCode, verwerkingId);

                try
                {
                    await SqlEmailPersistenceRepository.UpdateStatusAsync(connectionString, verwerkingId, EmailStatus.Review, null);
                }
                catch (Exception statusEx)
                {
                    log.LogWarning(statusEx,
                        "Kon Review-status niet vastleggen voor verwerking {Id} — de onbesliste verzendintentie "
                        + "blijft staan, dus een volgende poll legt het bericht alsnog ter beoordeling neer",
                        verwerkingId);
                }

                try
                {
                    await graphService.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
                    await graphService.SetCategoriesAsync(email.MessageId, EmailCategorieLabels.GeenAiAntwoord);
                }
                catch (Exception labelEx)
                {
                    log.LogWarning(labelEx, "Outlook-labeling mislukt voor verwerking {Id}", verwerkingId);
                }

                // Wél als gelezen markeren: dit is een terminale beoordelingsstatus, geen fout die de
                // volgende poll opnieuw moet oppakken via de wachtrij van ongelezen berichten.
                await graphService.MarkAsReadAsync(email.MessageId);
                return ReplyVerwerkingUitkomst.OnbekendeVerzendUitkomst;
            }

            log.LogError(ex,
                "Graph-send expliciet afgewezen (status {StatusCode}) voor verwerking {Id} — VerzendFout, mail blijft ongelezen",
                graphStatusCode, verwerkingId);
            // Het versturen is aantoonbaar mislukt, dus de intentie moet weg: anders is dit scenario —
            // waarin juist wél opnieuw geprobeerd moet worden — niet te onderscheiden van een
            // onbekende uitkomst en belandt het bericht onnodig op Review.
            try { await SqlEmailPersistenceRepository.WisVerzendPogingAsync(connectionString, verwerkingId); }
            catch (Exception wisEx)
            {
                log.LogWarning(wisEx,
                    "Verzendintentie kon niet gewist worden voor verwerking {Id} — een volgende poll legt dit "
                    + "bericht ter beoordeling neer in plaats van opnieuw te versturen", verwerkingId);
            }
            try { await SqlEmailPersistenceRepository.UpdateFoutAsync(connectionString, verwerkingId, sanitizeFoutMelding(ex.Message)); }
            catch (Exception logEx) { log.LogWarning(logEx, "Kon foutstatus niet vastleggen voor verwerking {Id}", verwerkingId); }
            return ReplyVerwerkingUitkomst.VerzendFout;
        }

        // Vanaf hier is het antwoord de deur uit. Faalt het vastleggen, dan mag het bericht NIET
        // ongelezen blijven: de volgende poll zou de afzender een tweede antwoord sturen. Daarom
        // wordt de fout alleen gelogd en gaat het als-gelezen-markeren altijd door.
        try
        {
            await SqlEmailPersistenceRepository.UpdateAntwoordVerstuurdAsync(connectionString, verwerkingId, email.Afzender, antwoordBody);
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

    /// <summary>
    /// Haalt de HTTP-statuscode uit een Graph-foutrespons (#1133), of <c>null</c> als de exception
    /// geen respons van Graph vertegenwoordigt — bijv. een time-out of verbindingsfout vóórdat er
    /// ooit een respons was. Alleen deze statuscode bepaalt, via
    /// <see cref="EmailVerzendFoutClassificatie.Classificeer"/>, of een verzendfout als expliciete
    /// afwijzing mag gelden.
    /// </summary>
    private static int? ExtraheerGraphStatusCode(Exception ex)
        => ex is Microsoft.Graph.Models.ODataErrors.ODataError odataError && odataError.ResponseStatusCode > 0
            ? odataError.ResponseStatusCode
            : null;
}
