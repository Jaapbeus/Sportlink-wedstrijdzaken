using Planner.Shared.Email;
using Newtonsoft.Json;
using SportlinkFunction.Processing;
using Planner.Shared;
using Planner.Shared.Email.Trace;
using Microsoft.Extensions.Logging;
using SportlinkFunction.TeamResolution;

namespace SportlinkFunction.Email;

public partial class EmailProcessorFunction
{
    private static async Task VerwerkEmailAsync(
        InkomendBericht email,
        BerichtClassificatie classificatie,
        IEmailGraphService graphService,
        IReadOnlySet<string> uitgeslotenAdressen,
        BerichtAiService aiService,
        IEmailPersistenceService persistenceService,
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

        if (await BepaalVerwerkingIdAsync(email, graphService, persistenceService, log) is not int verwerkingId)
            return;

        classificatie = await DetecteerLeermomentAsync(verwerkingId, email, classificatie, persistenceService, aiService, log);
        var classificatieJson = JsonConvert.SerializeObject(classificatie);

        if (await VerwerkBuitenScopeAsync(
                verwerkingId, email, classificatie, classificatieJson, graphService, persistenceService, log))
            return;

        if (await EmailVerwerkingKoppeling.HandelAntwoordLimietAsync(
                verwerkingId, email, classificatie, classificatieJson, graphService, persistenceService, log))
            return;

        await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geregistreerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, LogWaarde.Schoon(classificatie.Datum));

        var replyUitkomst = await VoerReplyFlowMetTraceAsync(
            verwerkingId, email, classificatie, graphService, persistenceService, replyPolicyService, teamResolver, log);

        if (replyUitkomst != ReplyVerwerkingUitkomst.AntwoordVerstuurd)
            return;

        await StuurVervolgNotificatiesAsync(classificatie, email, graphService, log);
    }

    /// <summary>
    /// Leermomenten rond één bericht: reply-/correctiedetectie en few-shot herclassificatie als er
    /// gevalideerde leermomenten zijn. Retourneert de (eventueel herziene) classificatie.
    /// </summary>
    private static async Task<BerichtClassificatie> DetecteerLeermomentAsync(
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        IEmailPersistenceService persistenceService,
        BerichtAiService aiService,
        ILogger log)
    {
        // Reply-detectie — is dit een reply op een eerder door ons beantwoord bericht?
        await DetecteerEnRegistreerCorrectieAsync(verwerkingId, email, persistenceService, aiService, log);

        // Few-shot herclassificatie als er gevalideerde leermomenten zijn
        var voorbeelden = await persistenceService.HaalLeermomentVoorbeeldenOpAsync(log);
        if (voorbeelden.Count == 0)
            return classificatie;

        try
        {
            classificatie = await aiService.ClassificeerBerichtAsync(
                email.Body, email.Onderwerp, email.Afzender, voorbeelden);
            BerichtPipeline.ValideerDagDatum(classificatie, email.Body, email.Onderwerp);
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
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        string classificatieJson,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
    {
        if (!await HandelBuitenScopeAsync(
                verwerkingId, email.MessageId, classificatie, classificatieJson, graphService, persistenceService, log))
            return false;

        await BewaarTraceVeiligAsync(persistenceService, verwerkingId, classificatie.Type.ToString(),
            EmailTraceOpslag.BouwKorteTrace(classificatie.Type.ToString(), classificatie.TeamNaam, classificatie.Tegenstander,
                classificatie.GetAlleDatums().Count, classificatie.AanvangsTijd, EmailTraceOpslag.BuitenScopeReden), log);
        return true;
    }

    /// <summary>
    /// Planner-verwerking en reply-flow van één bericht. #1568: de beslissingstrace wordt per
    /// bericht opgebouwd en altijd (ook bij een fout) bewaard.
    /// </summary>
    private static async Task<ReplyVerwerkingUitkomst> VoerReplyFlowMetTraceAsync(
        int verwerkingId,
        InkomendBericht email,
        BerichtClassificatie classificatie,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        EmailReplyPolicyService replyPolicyService,
        ITeamResolver teamResolver,
        ILogger log)
    {
        var trace = new TraceBuilder();
        return await EmailTraceOpslag.MetTraceAsync(trace, async () =>
        {
            var plannerResponseJson = await BerichtPipeline.VerwerkMetPlannerAsync(
                classificatie, email, log, teamResolver, trace: trace);
            await persistenceService.UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);
            await persistenceService.UpdateStatusAsync(verwerkingId, EmailStatus.Verwerkt, null);

            var reviewMode = EmailReviewModus.IsActief();
            var reviewRecipient = Environment.GetEnvironmentVariable("EmailReviewRecipient");
            return await replyPolicyService.HandelReplyFlowAfAsync(
                verwerkingId,
                email,
                classificatie,
                plannerResponseJson,
                reviewMode,
                reviewRecipient,
                graphService,
                persistenceService,
                () => BerichtPipeline.BouwTemplateAntwoord(classificatie, plannerResponseJson, email, log, trace: trace),
                SanitizeFoutMelding,
                log,
                trace,
                await ZekerheidspoortInstelling.IsActiefAsync(persistenceService.ResolveClubCode(), log));
        }, async t =>
            {
                // #1568 deel C: onbekende teamteksten naar de wachtrij; een fout hier breekt de trace-opslag niet.
                await OnbekendeTeamTekstOpslag.BewaarVeiligAsync(t, persistenceService.ResolveClubCode(), verwerkingId,
                    new Admin.SqlOnbekendeTeamTekstStore(SystemUtilities.DatabaseConfig.ConnectionString), log);
                await persistenceService.UpsertTraceAsync(
                    verwerkingId, classificatie.Type.ToString(), t, EmailTraceRecord.VersieVan(typeof(EmailProcessorFunction).Assembly));
            },
            log, verwerkingId);
    }

    /// <summary>
    /// Bewaart de PII-arme beslissingstrace (#1568). Een mislukte opslag mag de verwerking nooit
    /// laten falen; gelogd wordt alleen het fouttype, nooit de foutmelding.
    /// </summary>
    internal static Task BewaarTraceVeiligAsync(
        IEmailPersistenceService persistenceService, int verwerkingId, string verzoekType,
        BeslissingsTrace trace, ILogger log)
        => EmailTraceOpslag.BewaarVeiligAsync(
            () => persistenceService.UpsertTraceAsync(
                verwerkingId, verzoekType, trace, EmailTraceRecord.VersieVan(typeof(EmailProcessorFunction).Assembly)),
            log, verwerkingId);

    /// <summary>
    /// Detecteert of dit bericht een reply is op een eerder door ons beantwoord bericht (#323) en,
    /// zo ja, of de afzender daarin een correctie geeft op de eerdere classificatie. Faalt de
    /// correctie-detectie zelf, dan gaat de hoofdverwerking gewoon door zonder correctie.
    /// </summary>
    private static async Task DetecteerEnRegistreerCorrectieAsync(
        int verwerkingId,
        InkomendBericht email,
        IEmailPersistenceService persistenceService,
        BerichtAiService aiService,
        ILogger log)
    {
        if (string.IsNullOrWhiteSpace(email.ConversationId))
            return;

        var (isReply, origineleVerwerkingId, origineelType, originaleSamenvatting) =
            await persistenceService.DetecteerReplyOpOnsAntwoordAsync(email.ConversationId, log);

        if (!isReply || !origineleVerwerkingId.HasValue)
            return;

        await persistenceService.UpdateReplyStatusAsync(verwerkingId, true, origineleVerwerkingId.Value);
        log.LogInformation("Email {Id} is reply op verwerking {OrigineleId}", verwerkingId, origineleVerwerkingId);

        // Detecteer of het een correctie is op de eerdere classificatie
        try
        {
            var (isCorrectie, afgeleidType, correctieSamenvatting) = await aiService.DetecteerCorrectieAsync(
                email.Body, email.Onderwerp, origineelType ?? "", originaleSamenvatting);

            if (isCorrectie)
            {
                await persistenceService.InsertClassificatieCorrectieAsync(
                    origineleVerwerkingId.Value, verwerkingId,
                    origineelType ?? "", afgeleidType,
                    originaleSamenvatting, correctieSamenvatting);
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
    /// team-contact-vraag naar de coach (#168).
    /// </summary>
    private static async Task StuurVervolgNotificatiesAsync(
        BerichtClassificatie classificatie,
        InkomendBericht email,
        IEmailGraphService graphService,
        ILogger log)
    {
        // Stuur interne notificatie naar teamleider bij herplanverzoeken van externe afzender (#66)
        if (classificatie.Type == VerzoekType.HerplanVerzoek
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam)
            && !string.IsNullOrWhiteSpace(classificatie.Datum))
        {
            await StuurTeamleiderNotificatieAsync(
                graphService, classificatie.TeamNaam, classificatie.Datum, log);
        }

        // Stuur vraag door naar coach bij team-contact-opvragen (#168)
        if (classificatie.Type == VerzoekType.TeamContactOpvragen
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam))
        {
            await StuurTeamContactBerichtDoorAsync(
                graphService, classificatie.TeamNaam, email, log);
        }
    }

    /// <summary>
    /// Bepaalt onder welk verwerkingId dit bericht verder verwerkt wordt, of <c>null</c> als de
    /// verwerking hier moet stoppen (#712); zie <see cref="EmailVerwerkingsAfhandeling.BepaalVerwerkingIdAsync"/> (#1650).
    /// </summary>
    internal static Task<int?> BepaalVerwerkingIdAsync(
        InkomendBericht email,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
        => EmailVerwerkingKoppeling.BepaalVerwerkingIdAsync(email, graphService, persistenceService, log);

    /// <summary>
    /// Handelt een buiten-scope classificatie af: status vastleggen, labelen, als gelezen markeren,
    /// géén antwoord. Retourneert <c>true</c> als het bericht hiermee is afgehandeld.
    /// <para>
    /// Nodig omdat de herclassificatie met leermomenten alsnog 'buiten scope' kan opleveren. Zo'n
    /// uitkomst kwam niet meer langs het BuitenScope-voorfilter van fase 1, waardoor er tóch een
    /// automatisch "vereist handmatige afhandeling"-antwoord uitging: identieke input, ander gedrag,
    /// puur afhankelijk van of er gevalideerde leermomenten in de database staan. (#712)
    /// </para>
    /// </summary>
    internal static Task<bool> HandelBuitenScopeAsync(
        int verwerkingId,
        string messageId,
        BerichtClassificatie classificatie,
        string classificatieJson,
        IEmailGraphService graphService,
        IEmailPersistenceService persistenceService,
        ILogger log)
        => EmailVerwerkingKoppeling.HandelBuitenScopeAsync(
            verwerkingId, messageId, classificatie.Type == VerzoekType.BuitenScope, classificatieJson,
            graphService, persistenceService, log);
}
