using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Planner.Shared;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using Planner.Shared.TeamResolution;

namespace Planner.Endpoints.Email;

/// <summary>Tier-onafhankelijke control flow voor de zware fase van één e-mailbericht.</summary>
internal static class EmailBerichtVerwerking
{
    internal static async Task VerwerkAsync<TBericht>(
        TBericht bericht, BerichtClassificatie classificatie, IReadOnlySet<string> uitgeslotenAdressen,
        ILogger log, IEmailBerichtPoort<TBericht> poort)
    {
        if (uitgeslotenAdressen.Contains(poort.Afzender(bericht)))
        {
            log.LogInformation("Email {MessageId} van uitgesloten adres (verse lijst), overslaan (afzender niet gelogd — AVG #210)", poort.MessageId(bericht));
            await poort.MarkeerGelezenAsync(poort.MessageId(bericht));
            return;
        }

        if (await poort.BepaalVerwerkingIdAsync(bericht, log) is not int verwerkingId)
            return;

        await DetecteerEnRegistreerCorrectieAsync(bericht, verwerkingId, poort, log);
        var classificatieJson = JsonConvert.SerializeObject(classificatie);

        if (await VerwerkBuitenScopeAsync(bericht, verwerkingId, classificatie, classificatieJson, log, poort))
            return;

        if (await poort.HandelAntwoordLimietAsync(verwerkingId, bericht, classificatie, classificatieJson, log))
            return;

        await poort.UpdateStatusAsync(verwerkingId, EmailStatus.Geclassificeerd, classificatieJson);
        log.LogInformation("Email {Id} geregistreerd als {Type}, datum={Datum}",
            verwerkingId, classificatie.Type, LogWaarde.Schoon(classificatie.Datum));

        var uitkomst = await VoerReplyFlowMetTraceAsync(bericht, verwerkingId, classificatie, log, poort);
        if (uitkomst != ReplyVerwerkingUitkomst.AntwoordVerstuurd)
            return;

        if (classificatie.Type == VerzoekType.HerplanVerzoek
            && !string.IsNullOrWhiteSpace(classificatie.TeamNaam)
            && !string.IsNullOrWhiteSpace(classificatie.Datum))
            await poort.StuurTeamleiderNotificatieAsync(classificatie, log);
    }

    private static async Task<bool> VerwerkBuitenScopeAsync<TBericht>(
        TBericht bericht, int verwerkingId, BerichtClassificatie classificatie, string classificatieJson,
        ILogger log, IEmailBerichtPoort<TBericht> poort)
    {
        if (classificatie.Type != VerzoekType.BuitenScope)
            return false;

        await poort.UpdateStatusAsync(verwerkingId, EmailStatus.BuitenScope, classificatieJson);
        log.LogInformation("Email {Id} buiten scope na herclassificatie — geen antwoord verstuurd", verwerkingId);
        try
        {
            await poort.Graph.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
            await poort.Graph.SetCategoriesAsync(poort.MessageId(bericht), EmailCategorieLabels.GeenAiAntwoord);
            await poort.MarkeerGelezenAsync(poort.MessageId(bericht));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Outlook-labeling buiten scope mislukt voor {MessageId} — status is wel vastgelegd", poort.MessageId(bericht));
        }
        var korteTrace = EmailTraceOpslag.BouwKorteTrace(classificatie.Type.ToString(), classificatie.TeamNaam,
            classificatie.Tegenstander, classificatie.GetAlleDatums().Count, classificatie.AanvangsTijd,
            EmailTraceOpslag.BuitenScopeReden);
        await EmailTraceOpslag.BewaarVeiligAsync(
            () => poort.BewaarKorteTraceAsync(verwerkingId, classificatie.Type.ToString(), korteTrace, log),
            log, verwerkingId);
        return true;
    }

    private static async Task<ReplyVerwerkingUitkomst> VoerReplyFlowMetTraceAsync<TBericht>(
        TBericht bericht, int verwerkingId, BerichtClassificatie classificatie, ILogger log,
        IEmailBerichtPoort<TBericht> poort)
    {
        var trace = new TraceBuilder();
        return await EmailTraceOpslag.MetTraceAsync(trace, async () =>
        {
            var plannerResponseJson = await poort.VerwerkMetPlannerAsync(bericht, classificatie, trace, log);
            await poort.UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);
            return await TeamHandmatigeAfhandeling.HandelOfVervolgAsync(classificatie, verwerkingId,
                poort.MessageId(bericht), poort.Persistentie, poort.Graph, async () =>
                {
                    await poort.UpdateStatusAsync(verwerkingId, EmailStatus.Verwerkt, null);
                    var reviewMode = EmailReviewModus.IsActief();
                    var reviewRecipient = Environment.GetEnvironmentVariable("EmailReviewRecipient");
                    return await poort.HandelReplyFlowAfAsync(verwerkingId, bericht, classificatie,
                        plannerResponseJson, reviewMode, reviewRecipient,
                        () => poort.BouwTemplateAntwoordAsync(bericht, classificatie, plannerResponseJson, log, trace),
                        poort.SanitizeFoutMelding, log, trace, await poort.ZekerheidspoortActiefAsync(log));
                }, log);
        }, async t =>
        {
            await poort.BewaarOnbekendeTeamtekstenAsync(t, verwerkingId, log);
            await poort.BewaarTraceAsync(verwerkingId, classificatie.Type.ToString(), t);
        }, log, verwerkingId);
    }

    private static async Task DetecteerEnRegistreerCorrectieAsync<TBericht>(
        TBericht bericht, int verwerkingId, IEmailBerichtPoort<TBericht> poort, ILogger log)
    {
        var conversationId = poort.ConversationId(bericht);
        if (string.IsNullOrWhiteSpace(conversationId)) return;

        var (isReply, origineelId, origineelType, origineleSamenvatting) =
            await poort.DetecteerReplyAsync(conversationId, log);
        if (!isReply || !origineelId.HasValue) return;

        await poort.UpdateReplyStatusAsync(verwerkingId, origineelId.Value);
        log.LogInformation("Email {Id} is reply op verwerking {OrigineleId}", verwerkingId, origineelId);
        try
        {
            var (isCorrectie, juistType, samenvatting) = await poort.DetecteerCorrectieAsync(
                bericht, origineelType ?? "", origineleSamenvatting);
            if (!isCorrectie) return;

            await poort.BewaarCorrectieAsync(origineelId.Value, verwerkingId, origineelType ?? "",
                juistType, origineleSamenvatting, samenvatting);
            log.LogInformation("Correctie gedetecteerd voor verwerking {OrigineleId}: {OrigineelType} → {JuistType}",
                origineelId, LogWaarde.Schoon(origineelType), LogWaarde.Schoon(juistType));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Correctie-detectie mislukt voor reply {Id} — doorgaan zonder correctie", verwerkingId);
        }
    }
}
