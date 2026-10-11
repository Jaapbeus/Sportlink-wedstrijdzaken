using Planner.Shared.Email;
using FunctionApp.Postgres.Admin;
using Newtonsoft.Json;
using FunctionApp.Postgres.Processing;
using Planner.Shared;
using Planner.Shared.Email.Trace;
using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.TeamResolution;

namespace FunctionApp.Postgres.Email;

public partial class EmailProcessorFunction
{
    private static async Task VerwerkEmailAsync(
        string cs,
        string clubCode,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        IEmailGraphService graphService,
        IReadOnlySet<string> uitgeslotenAdressen,
        BerichtAiService aiService,
        EmailReplyPolicyService replyPolicyService,
        ILogger log,
        ITeamResolver teamResolver)
    {
        // Hercheck met verse DB-geladen uitsluitingslijst (kan afwijken van cache)
        if (uitgeslotenAdressen.Contains(email.Afzender))
        {
            log.LogInformation("Email {MessageId} van uitgesloten adres (verse lijst), overslaan (afzender niet gelogd — AVG #210)", email.MessageId);
            await graphService.MarkAsReadAsync(email.MessageId);
            return;
        }

        if (await BepaalVerwerkingIdAsync(cs, clubCode, email, graphService, log) is not int verwerkingId)
            return;

        classificatie = await DetecteerLeermomentAsync(cs, clubCode, verwerkingId, email, classificatie, aiService, log);
        var classificatieJson = JsonConvert.SerializeObject(classificatie);

        if (await VerwerkBuitenScopeAsync(
                cs, clubCode, verwerkingId, email, classificatie, classificatieJson, graphService, log))
            return;

        await SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geregistreerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, LogWaarde.Schoon(classificatie.Datum));

        var replyUitkomst = await VoerReplyFlowMetTraceAsync(
            cs, clubCode, verwerkingId, email, classificatie, graphService, replyPolicyService, teamResolver, log);

        if (replyUitkomst != ReplyVerwerkingUitkomst.AntwoordVerstuurd)
            return;

        await StuurVervolgNotificatiesAsync(cs, clubCode, classificatie, email, graphService, log);
    }

    /// <summary>
    /// Leermomenten rond één bericht: reply-/correctiedetectie en few-shot herclassificatie als er
    /// gevalideerde leermomenten zijn. Retourneert de (eventueel herziene) classificatie.
    /// </summary>
    private static async Task<BerichtClassificatie> DetecteerLeermomentAsync(
        string cs,
        string clubCode,
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        BerichtAiService aiService,
        ILogger log)
    {
        // Reply-detectie — is dit een reply op een eerder door ons beantwoord bericht?
        await DetecteerEnRegistreerCorrectieAsync(cs, clubCode, verwerkingId, email, aiService, log);

        // Few-shot herclassificatie als er gevalideerde leermomenten zijn
        var voorbeelden = await LearningMomentRepository.HaalVoorbeeldenOpAsync(cs, clubCode, log);
        if (voorbeelden.Count == 0)
            return classificatie;

        try
        {
            classificatie = await aiService.ClassificeerBerichtAsync(
                email.Body ?? "", email.Onderwerp, email.Afzender, voorbeelden);
            BerichtPipeline.ValideerDagDatum(classificatie, email.Body ?? "", email.Onderwerp);
            log.LogInformation("Email {Id} herclassificatie met {Aantal} leermomenten: {Type}",
                verwerkingId, voorbeelden.Count, classificatie.Type);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Herclassificatie met leermomenten mislukt voor {Id} — originele classificatie behouden", verwerkingId);
        }

        return classificatie;
    }

    /// <summary>
    /// Buiten-scope-afhandeling: statusregistratie, Outlook-label en korte trace. Retourneert
    /// <c>true</c> als het bericht buiten scope is en de verwerking hier eindigt.
    /// </summary>
    private static async Task<bool> VerwerkBuitenScopeAsync(
        string cs,
        string clubCode,
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string classificatieJson,
        IEmailGraphService graphService,
        ILogger log)
    {
        if (!await HandelBuitenScopeAsync(
                cs, verwerkingId, email.MessageId, classificatie, classificatieJson, graphService, log))
            return false;

        var korteTrace = EmailTraceOpslag.BouwKorteTrace(classificatie.Type.ToString(), classificatie.TeamNaam,
            classificatie.Tegenstander, classificatie.GetAlleDatums().Count, classificatie.AanvangsTijd, EmailTraceOpslag.BuitenScopeReden);
        await EmailTraceOpslag.BewaarVeiligAsync(() => EmailTraceRepository.UpsertAsync(cs, EmailTraceRecord.Van(
            verwerkingId, clubCode, classificatie.Type.ToString(), korteTrace,
            EmailTraceRecord.VersieVan(typeof(EmailProcessorFunction).Assembly))), log, verwerkingId);
        return true;
    }

    /// <summary>
    /// Planner-verwerking en reply-flow van één bericht. #1568: de beslissingstrace wordt per
    /// bericht opgebouwd en altijd (ook bij een fout) bewaard.
    /// </summary>
    private static async Task<ReplyVerwerkingUitkomst> VoerReplyFlowMetTraceAsync(
        string cs,
        string clubCode,
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        IEmailGraphService graphService,
        EmailReplyPolicyService replyPolicyService,
        ITeamResolver teamResolver,
        ILogger log)
    {
        var trace = new TraceBuilder();
        return await EmailTraceOpslag.MetTraceAsync(trace, async () =>
        {
            var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
                classificatie, email, log, teamResolver, clubCode, trace: trace);
            await SqlEmailPersistenceRepository.UpdatePlannerResponseAsync(cs, verwerkingId, plannerResponseJson);
            await SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.Verwerkt, null);

            var reviewMode = EmailReviewModus.IsActief();
            var reviewRecipient = Environment.GetEnvironmentVariable("EmailReviewRecipient");
            return await replyPolicyService.HandelReplyFlowAfAsync(
                cs,
                verwerkingId,
                email,
                classificatie,
                plannerResponseJson,
                reviewMode,
                reviewRecipient,
                graphService,
                () => BerichtPipeline.BouwTemplateAntwoord(classificatie, plannerResponseJson, email, log, null, clubCode, trace),
                SanitizeFoutMelding,
                log,
                trace,
                await ZekerheidspoortInstelling.IsActiefAsync(cs, clubCode, log));
        }, async t =>
            {
                // #1568 deel C: onbekende teamteksten naar de wachtrij; een fout hier breekt de trace-opslag niet.
                await OnbekendeTeamTekstOpslag.BewaarVeiligAsync(t, clubCode, verwerkingId, new PostgresOnbekendeTeamTekstStore(cs), log);
                await EmailTraceRepository.UpsertAsync(cs, EmailTraceRecord.Van(
                    verwerkingId, clubCode, classificatie.Type.ToString(), t,
                    EmailTraceRecord.VersieVan(typeof(EmailProcessorFunction).Assembly)));
            },
            log, verwerkingId);
    }

    /// <summary>
    /// Detecteert of dit bericht een reply is op een eerder door ons beantwoord bericht en, zo ja,
    /// of de afzender daarin een correctie geeft op de eerdere classificatie. Faalt de
    /// correctie-detectie zelf, dan gaat de hoofdverwerking gewoon door zonder correctie.
    /// </summary>
    private static async Task DetecteerEnRegistreerCorrectieAsync(
        string cs,
        string clubCode,
        int verwerkingId,
        InkomendBericht email,
        BerichtAiService aiService,
        ILogger log)
    {
        if (string.IsNullOrWhiteSpace(email.ConversationId))
            return;

        var (isReply, origineleVerwerkingId, origineelType, originaleSamenvatting) =
            await SqlEmailPersistenceRepository.DetecteerReplyOpOnsAntwoordAsync(cs, email.ConversationId, clubCode, log);

        if (!isReply || !origineleVerwerkingId.HasValue)
            return;

        await SqlEmailPersistenceRepository.UpdateReplyStatusAsync(cs, verwerkingId, true, origineleVerwerkingId.Value);
        log.LogInformation("Email {Id} is reply op verwerking {OrigineleId}", verwerkingId, origineleVerwerkingId);

        // Detecteer of het een correctie is op de eerdere classificatie
        try
        {
            var (isCorrectie, afgeleidType, correctieSamenvatting) = await aiService.DetecteerCorrectieAsync(
                email.Body ?? "", email.Onderwerp, origineelType ?? "", originaleSamenvatting);

            if (isCorrectie)
            {
                await LearningMomentRepository.InsertClassificatieCorrectieAsync(
                    cs, origineleVerwerkingId.Value, verwerkingId,
                    origineelType ?? "", afgeleidType,
                    originaleSamenvatting, correctieSamenvatting, clubCode);
                log.LogInformation("Correctie gedetecteerd voor verwerking {OrigineleId}: {OrigineelType} → {JuistType}",
                    origineleVerwerkingId, LogWaarde.Schoon(origineelType), LogWaarde.Schoon(afgeleidType));
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Correctie-detectie mislukt voor reply {Id} — doorgaan zonder correctie", verwerkingId);
        }
    }

    /// <summary>
    /// Stuurt de interne vervolgnotificaties die horen bij een succesvol verstuurd antwoord:
    /// teamleider-notificatie bij een herplanverzoek (#66) en het doorsturen van een
    /// team-contact-vraag naar de begeleider (#168). Zie de klassekop (item 4) voor de
    /// afwijking t.o.v. het SQL Server-origineel.
    /// </summary>
    private static async Task StuurVervolgNotificatiesAsync(
        string cs,
        string clubCode,
        BerichtClassificatie classificatie,
        InkomendBericht email,
        IEmailGraphService graphService,
        ILogger log)
    {
        if (classificatie.Type == VerzoekType.HerplanVerzoek
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam)
            && !string.IsNullOrWhiteSpace(classificatie.Datum))
        {
            await StuurTeamleiderNotificatieAsync(cs, clubCode, graphService, classificatie.TeamNaam!, classificatie.Datum!, log);
        }

        if (classificatie.Type == VerzoekType.TeamContactOpvragen
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam))
        {
            await StuurTeamContactBerichtDoorAsync(cs, clubCode, graphService, classificatie.TeamNaam!, email, log);
        }
    }

    /// <summary>
    /// Bepaalt onder welk verwerkingId dit bericht verder verwerkt wordt: hergebruik van een
    /// niet-afgeronde rij, of een nieuwe rij. Retourneert <c>null</c> als de verwerking hier moet
    /// stoppen.
    /// </summary>
    internal static async Task<int?> BepaalVerwerkingIdAsync(
        string cs,
        string clubCode,
        InkomendBericht email,
        IEmailGraphService graphService,
        ILogger log)
    {
        var stand = await SqlEmailPersistenceRepository.HaalVerwerkingStandOpAsync(cs, email.MessageId);

        switch (EmailIdempotentie.Bepaal(stand))
        {
            case VerwerkingsBesluit.OverslaanAlAfgerond:
                log.LogInformation("Email {MessageId} is al definitief afgehandeld (status {Status}), overslaan",
                    email.MessageId, stand!.Status);
                await graphService.MarkAsReadAsync(email.MessageId);
                return null;

            case VerwerkingsBesluit.OpgevenNaMaxPogingen:
                await GeefVerwerkingOpAsync(
                    cs, email, stand!.VerwerkingId, stand.Pogingen, "verwerking", graphService, log);
                return null;

            case VerwerkingsBesluit.OnbeslistNaVerzendPoging:
                await LegVoorBeoordelingNaVerzendPogingAsync(
                    cs, email, stand!.VerwerkingId, graphService, log);
                return null;

            case VerwerkingsBesluit.HerhaalVerwerking:
                await SqlEmailPersistenceRepository.VerhoogPogingenAsync(cs, stand!.VerwerkingId);
                log.LogWarning(
                    "Email {MessageId} was niet afgerond (status {Status}) — verwerking {Id} wordt hervat, poging {Poging} van {Max}",
                    email.MessageId, stand.Status, stand.VerwerkingId, stand.Pogingen + 1, EmailIdempotentie.MaxPogingen);
                return stand.VerwerkingId;

            default:
                try
                {
                    return await SqlEmailPersistenceRepository.InsertEmailVerwerkingAsync(cs, email, clubCode);
                }
                catch (DubbeleMessageIdException)
                {
                    log.LogWarning(
                        "Email {MessageId} is gelijktijdig door een andere verwerking geregistreerd — deze poging stopt zonder foutstatus",
                        email.MessageId);
                    return null;
                }
        }
    }

    /// <summary>
    /// Handelt een buiten-scope classificatie af: status vastleggen, labelen, als gelezen markeren,
    /// géén antwoord. Retourneert <c>true</c> als het bericht hiermee is afgehandeld.
    /// </summary>
    internal static async Task<bool> HandelBuitenScopeAsync(
        string cs,
        int verwerkingId,
        string messageId,
        BerichtClassificatie classificatie,
        string classificatieJson,
        IEmailGraphService graphService,
        ILogger log)
    {
        if (classificatie.Type != VerzoekType.BuitenScope)
            return false;

        await SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, EmailStatus.BuitenScope, classificatieJson);
        log.LogInformation("Email {Id} buiten scope na herclassificatie — geen antwoord verstuurd", verwerkingId);
        await LabelBuitenScopeAsync(graphService, messageId, log);
        return true;
    }

    private static async Task LabelBuitenScopeAsync(
        IEmailGraphService graphService, string messageId, ILogger log)
    {
        try
        {
            await graphService.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
            await graphService.SetCategoriesAsync(messageId, EmailCategorieLabels.GeenAiAntwoord);
            await graphService.MarkAsReadAsync(messageId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Outlook-labeling buiten scope mislukt voor {MessageId} — status is wel vastgelegd", messageId);
        }
    }
}
