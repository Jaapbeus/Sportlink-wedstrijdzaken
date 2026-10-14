using Microsoft.Extensions.Logging;
using Planner.Shared.Email.Trace;

namespace Planner.Shared.Email;

/// <summary>Wat er van het inkomende bericht nodig is voor de reply-afhandeling (de tier zet zijn eigen <c>InkomendBericht</c> hierop om).</summary>
public sealed record ReplyBericht(string MessageId, string Afzender, string? ConversationId);

public enum ReplyVerwerkingUitkomst
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

/// <summary>
/// De Graph-acties van de reply-afhandeling (#1760). Poort: <c>EmailGraphService</c> blijft per tier de
/// enige Graph-adapter (de tier-<c>IEmailGraphService</c> erft deze interface); de EgressGuard geldt daar.
/// </summary>
public interface IReplyGraph
{
    Task SetCategoriesAsync(string messageId, params string[] categories);
    Task EnsureMasterCategoryAsync(string name, string colorPreset);
    Task MarkAsReadAsync(string messageId);
    Task SendReplyAsync(string to, string subject, string body, string? conversationId,
        IReadOnlyList<string>? bcc = null, EmailBijlage? bijlage = null);
}

/// <summary>
/// De opslagnaad van de reply-afhandeling (#1568, #1760): precies de zes schrijfacties. De tier bindt
/// hem aan zijn eigen persistentie (SQL Server: <c>IEmailPersistenceService</c> erft hem; Postgres:
/// <c>SqlReplyPersistentie</c>).
/// </summary>
public interface IReplyPersistentie
{
    Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? geextraheerdeData);
    Task UpdateAntwoordVerstuurdAsync(int verwerkingId, string verstuurdNaar, string antwoordEmail);

    /// <summary>
    /// Slaat een voorgesteld antwoord op zonder het te versturen (review mode) en zet de status op
    /// <see cref="EmailStatus.Review"/>. Vult bewust géén <c>VerstuurdNaar</c> — er is niets
    /// verstuurd, en die kolom is de duplicaatgrens van de idempotentie-guard.
    /// </summary>
    Task UpdateVoorgesteldAntwoordAsync(int verwerkingId, string antwoordEmail);

    /// <summary>
    /// Legt de intentie vast om te gaan versturen, vóór de verzendpoging (#716). Zie
    /// <see cref="WisVerzendPogingAsync"/> voor het wissen bij een aantoonbaar mislukte poging.
    /// </summary>
    Task MarkeerVerzendPogingAsync(int verwerkingId);

    /// <summary>
    /// Wist de verzendintentie omdat het versturen aantoonbaar is mislukt (#716). Alleen dán mag een
    /// volgende poging opnieuw versturen; blijft de intentie staan, dan is de uitkomst onbekend.
    /// </summary>
    Task WisVerzendPogingAsync(int verwerkingId);

    /// <summary>
    /// Legt een verwerkingsfout vast op <b>verwerkingId</b> (#717), consistent met alle andere
    /// mutaties. Overschrijft nooit een rij waarvan al vaststaat dat er een antwoord op is verstuurd.
    /// </summary>
    Task UpdateFoutAsync(int verwerkingId, string foutMelding);
}

/// <summary>
/// Wat per tier blijft bij de reply-afhandeling (#1760): databasetoegang voor het begeleidingscontact,
/// de KNVB-kalenderbijlage (seizoen + PDF) en het uitlezen van de Graph-statuscode (Graph SDK).
/// </summary>
/// <param name="ZoekBegeleidingEmailAsync">E-mailadres van de begeleiding van het team (voor de BCC), of <c>null</c>.</param>
/// <param name="HaalKnvbBijlageAsync">De KNVB-kalender-PDF voor de regio, of <c>null</c> als er geen seizoen of bestand is.</param>
/// <param name="GraphStatusCode">HTTP-statuscode van een Graph-foutrespons, of <c>null</c> als de fout geen respons is (#1133).</param>
public sealed record ReplyTierHaken(
    Func<string, Task<string?>> ZoekBegeleidingEmailAsync,
    Func<string, Task<EmailBijlage?>> HaalKnvbBijlageAsync,
    Func<Exception, int?> GraphStatusCode);

/// <summary>
/// Tier-onafhankelijke beslislogica van de reply-flow (#1760, #1716 slice 8; voorheen
/// <c>EmailReplyPolicyService</c> per tier): review mode, onderdrukken, zekerheidspoort,
/// verzendintentie vóór het versturen (#716), classificatie van een verzendfout (#1133) en het
/// vastleggen. Persistentie en Graph lopen via <see cref="IReplyPersistentie"/> en
/// <see cref="IReplyGraph"/>.
/// </summary>
public static class EmailReplyAfhandeling
{
    private const string HandmatigePlanningLabel = "Handmatige planning";

    public static async Task<ReplyVerwerkingUitkomst> HandelReplyFlowAfAsync(
        int verwerkingId,
        ReplyBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        bool reviewMode,
        string? reviewRecipient,
        IReplyGraph graphService,
        IReplyPersistentie persistentie,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ReplyTierHaken haken,
        ILogger log,
        TraceBuilder? trace = null,
        bool zekerheidspoortActief = true)
    {
        // Review mode blijft de eerste check: er gaat nooit een antwoord naar de originele
        // afzender. Het voorgestelde antwoord wordt opgebouwd en opgeslagen — zonder dat valt er
        // niets te reviewen. Daarnaast wordt hetzelfde voorstel ook gemaild naar
        // reviewRecipient (#801-precedent): zonder deze mail is het voorstel alleen via directe
        // databasetoegang te lezen, omdat de Admin GUI AntwoordEmail bewust nooit teruggeeft (AVG).
        // Een mislukte reviewmail blokkeert de opslag en labeling niet.
        return reviewMode
            ? await HandelReviewModeAsync(
                verwerkingId, email, classificatie, plannerResponseJson, reviewRecipient,
                graphService, persistentie, bouwTemplateAntwoordAsync, sanitizeFoutMelding, log)
            : await HandelNormaalVerstuurAsync(
                verwerkingId, email, classificatie, plannerResponseJson,
                graphService, persistentie, bouwTemplateAntwoordAsync, sanitizeFoutMelding, haken, log, trace, zekerheidspoortActief, reviewRecipient);
    }

    private static async Task<ReplyVerwerkingUitkomst> HandelReviewModeAsync(
        int verwerkingId,
        ReplyBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        string? reviewRecipient,
        IReplyGraph graphService,
        IReplyPersistentie persistentie,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ILogger log,
        bool tegengehouden = false)
    {
        var reviewBesluit = ReplyPolicy.Bepaal(classificatie, plannerResponseJson);
        if (reviewBesluit.MoetVersturen)
        {
            var (voorgesteldOnderwerp, voorgesteldeBody) = await bouwTemplateAntwoordAsync();
            await persistentie.UpdateVoorgesteldAntwoordAsync(verwerkingId, voorgesteldeBody);
            log.LogInformation(
                "Email {Id} review mode — voorgesteld antwoord opgeslagen ter beoordeling", verwerkingId);

            if (!string.IsNullOrWhiteSpace(reviewRecipient))
            {
                try
                {
                    await graphService.SendReplyAsync(reviewRecipient, voorgesteldOnderwerp,
                        tegengehouden ? ZekerheidsPoort.MetReviewKop(voorgesteldeBody) : voorgesteldeBody, email.ConversationId);
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
            // Onderdrukken is een beleidsbesluit (#572), geen AI-uitval: zelfde status als buiten review mode (#1608).
            await persistentie.UpdateStatusAsync(verwerkingId, EmailStatus.GeenAntwoordNodig, null);
            log.LogInformation(
                "Email {Id} review mode — geen antwoord nodig, handmatige planning: {Reden}", verwerkingId, reviewBesluit.Reden);
        }

        try
        {
            // Een voorstel dat op beoordeling wacht krijgt geen label (#1244). "Geen AI antwoord" alleen bij een
            // door de zekerheidspoort tegengehouden voorstel (#1568); een onderdrukt antwoord krijgt
            // "Handmatige planning", net als buiten review mode (#1608).
            var (label, kleur) = tegengehouden ? (EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur)
                : reviewBesluit.MoetVersturen ? (null, null) : (HandmatigePlanningLabel, "preset5");
            if (label != null)
            {
                await graphService.EnsureMasterCategoryAsync(label, kleur!);
                await graphService.SetCategoriesAsync(email.MessageId, label);
            }

            await graphService.MarkAsReadAsync(email.MessageId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Graph-categorie mislukt voor verwerking {Id} in review mode", verwerkingId);
            try { await persistentie.UpdateFoutAsync(verwerkingId, sanitizeFoutMelding(ex.Message)); }
            catch (Exception logEx) { log.LogWarning(logEx, "Kon foutstatus niet vastleggen voor verwerking {Id}", verwerkingId); }
        }

        return ReplyVerwerkingUitkomst.AfgerondZonderAntwoord;
    }

    private static async Task<ReplyVerwerkingUitkomst> HandelNormaalVerstuurAsync(
        int verwerkingId,
        ReplyBericht email,
        BerichtClassificatie classificatie,
        string plannerResponseJson,
        IReplyGraph graphService,
        IReplyPersistentie persistentie,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAntwoordAsync,
        Func<string, string> sanitizeFoutMelding,
        ReplyTierHaken haken,
        ILogger log,
        TraceBuilder? trace,
        bool zekerheidspoortActief,
        string? reviewRecipient)
    {
        var replyBesluit = ReplyPolicy.Bepaal(classificatie, plannerResponseJson);
        if (!replyBesluit.MoetVersturen)
        {
            await persistentie.UpdateStatusAsync(verwerkingId, EmailStatus.GeenAntwoordNodig, null);
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

        // Zekerheidspoort (#1568 deel D): een onzeker antwoord gaat niet naar de afzender maar ter review.
        var poort = ZekerheidsPoort.Bepaal(zekerheidspoortActief, trace);
        if (poort.Tegenhouden)
            return await HandelReviewModeAsync(
                verwerkingId, email, classificatie, plannerResponseJson, reviewRecipient,
                graphService, persistentie, () => Task.FromResult((onderwerp, antwoordBody)), sanitizeFoutMelding, log, tegengehouden: true);

        // Verzendintentie vóór het versturen. Wordt de invocatie hierna hard afgebroken
        // (functie-time-out, host-recycle, scale-in), dan is dit het enige spoor dat er misschien al
        // een antwoord de deur uit is — de volgende poll stuurt dan geen tweede antwoord meer maar legt
        // het bericht ter beoordeling neer. Mislukt het vastleggen van de intentie zelf, dan wordt er
        // niet verstuurd: zonder die grens is een dubbel antwoord mogelijk.
        await persistentie.MarkeerVerzendPogingAsync(verwerkingId);

        IReadOnlyList<string>? bcc = null;
        EmailBijlage? bijlage = null;
        if (classificatie.VoegKnvbPdfBijlageToe)
        {
            // #561/#1141: verzet-zonder-datum — begeleiding van ons eigen team in BCC, KNVB-
            // kalender als bijlage. Beide zijn fail-safe: ontbreekt het contact of het bestand, dan
            // verstuurt de mail gewoon zonder (nooit een crash op deze verrijking).
            try
            {
                var begeleidingEmail = await haken.ZoekBegeleidingEmailAsync(classificatie.TeamNaam ?? "");
                if (!string.IsNullOrWhiteSpace(begeleidingEmail))
                {
                    bcc = new[] { begeleidingEmail };
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
                bijlage = await haken.HaalKnvbBijlageAsync(classificatie.KnvbBijlageRegio);
        }

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
            var graphStatusCode = haken.GraphStatusCode(ex);
            var uitkomst = EmailVerzendFoutClassificatie.Classificeer(ex, graphStatusCode);

            if (uitkomst == EmailVerzendUitkomst.OnbekendeUitkomst)
            {
                log.LogError(ex,
                    "Graph-send onbekende uitkomst (status {StatusCode}) voor verwerking {Id} — Graph kan het "
                    + "bericht al hebben geaccepteerd. Verzendintentie blijft staan, NIET opnieuw versturen — "
                    + "bericht direct op Review gezet (#1133)", graphStatusCode, verwerkingId);

                try
                {
                    await persistentie.UpdateStatusAsync(verwerkingId, EmailStatus.Review, null);
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
            try { await persistentie.WisVerzendPogingAsync(verwerkingId); }
            catch (Exception wisEx)
            {
                log.LogWarning(wisEx,
                    "Verzendintentie kon niet gewist worden voor verwerking {Id} — een volgende poll legt dit "
                    + "bericht ter beoordeling neer in plaats van opnieuw te versturen", verwerkingId);
            }
            try { await persistentie.UpdateFoutAsync(verwerkingId, sanitizeFoutMelding(ex.Message)); }
            catch (Exception logEx) { log.LogWarning(logEx, "Kon foutstatus niet vastleggen voor verwerking {Id}", verwerkingId); }
            return ReplyVerwerkingUitkomst.VerzendFout;
        }

        // Vanaf hier is het antwoord de deur uit. Faalt het vastleggen, dan mag het bericht NIET
        // ongelezen blijven: de volgende poll zou de afzender een tweede antwoord sturen. Daarom
        // wordt de fout alleen gelogd en gaat het als-gelezen-markeren altijd door.
        try
        {
            await persistentie.UpdateAntwoordVerstuurdAsync(verwerkingId, email.Afzender, antwoordBody);
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
