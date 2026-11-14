using Microsoft.Extensions.Logging;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using Planner.Shared.TeamResolution;
using Planner.Shared;
using SportlinkFunction.Processing;

namespace SportlinkFunction.Email;

internal sealed class EmailBerichtPoort(
    IEmailGraphService graph, IEmailPersistenceService persistence, BerichtAiService ai,
    EmailReplyPolicyService replyPolicy, ITeamResolver teamResolver)
    : IEmailBerichtPoort<InkomendBericht>
{
    private string AppVersie => EmailTraceRecord.VersieVan(typeof(EmailProcessorFunction).Assembly);
    public IReplyGraph Graph => graph;
    public IReplyPersistentie Persistentie => persistence;
    public Func<string, string> SanitizeFoutMelding => EmailSanitizer.SanitizeFoutMelding;
    public string MessageId(InkomendBericht bericht) => bericht.MessageId;
    public string Afzender(InkomendBericht bericht) => bericht.Afzender;
    public string? ConversationId(InkomendBericht bericht) => bericht.ConversationId;
    public Task MarkeerGelezenAsync(string messageId) => graph.MarkAsReadAsync(messageId);
    public Task<int?> BepaalVerwerkingIdAsync(InkomendBericht bericht, ILogger logger)
        => EmailVerwerkingKoppeling.BepaalVerwerkingIdAsync(bericht, graph, persistence, logger);
    public Task<(bool IsReply, int? OrigineelId, string? OrigineelType, string? OrigineleSamenvatting)> DetecteerReplyAsync(string conversationId, ILogger logger)
        => persistence.DetecteerReplyOpOnsAntwoordAsync(conversationId, logger);
    public Task UpdateReplyStatusAsync(int verwerkingId, int origineelId)
        => persistence.UpdateReplyStatusAsync(verwerkingId, true, origineelId);
    public Task<(bool IsCorrectie, string? JuistType, string? Samenvatting)> DetecteerCorrectieAsync(InkomendBericht bericht, string origineelType, string? origineleSamenvatting)
        => ai.DetecteerCorrectieAsync(Body(bericht), bericht.Onderwerp, origineelType, origineleSamenvatting);
    public Task BewaarCorrectieAsync(int origineelId, int verwerkingId, string origineelType, string? juistType,
        string? origineleSamenvatting, string? samenvatting)
        => persistence.InsertClassificatieCorrectieAsync(origineelId, verwerkingId, origineelType, juistType, origineleSamenvatting, samenvatting);
    public Task<bool> HandelBuitenScopeAsync(int verwerkingId, InkomendBericht bericht,
        BerichtClassificatie classificatie, string classificatieJson, ILogger logger)
        => EmailVerwerkingKoppeling.HandelBuitenScopeAsync(verwerkingId, bericht.MessageId,
            classificatie.Type == VerzoekType.BuitenScope, classificatieJson, graph, persistence, logger);
    public Task<bool> HandelAntwoordLimietAsync(int verwerkingId, InkomendBericht bericht, BerichtClassificatie classificatie, string classificatieJson, ILogger logger)
        => EmailVerwerkingKoppeling.HandelAntwoordLimietAsync(verwerkingId, bericht, classificatie, classificatieJson, graph, persistence, logger);
    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? classificatieJson)
        => persistence.UpdateStatusAsync(verwerkingId, status, classificatieJson);
    public Task<string> VerwerkMetPlannerAsync(InkomendBericht bericht, BerichtClassificatie classificatie, TraceBuilder trace, ILogger logger)
        => BerichtPipeline.VerwerkMetPlannerAsync(classificatie, bericht, logger, teamResolver, trace: trace);
    public Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson)
        => persistence.UpdatePlannerResponseAsync(verwerkingId, plannerResponseJson);
    public Task<ReplyVerwerkingUitkomst> HandelReplyFlowAfAsync(int verwerkingId, InkomendBericht bericht,
        BerichtClassificatie classificatie, string plannerResponseJson, bool reviewMode, string? reviewRecipient,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAsync, Func<string, string> sanitizeFoutMelding,
        ILogger logger, TraceBuilder trace, bool zekerheidspoortActief)
        => replyPolicy.HandelReplyFlowAfAsync(verwerkingId, bericht, classificatie, plannerResponseJson,
            reviewMode, reviewRecipient, graph, persistence, bouwTemplateAsync, sanitizeFoutMelding,
            logger, trace, zekerheidspoortActief);
    public Task<bool> ZekerheidspoortActiefAsync(ILogger logger)
        => ZekerheidspoortInstelling.IsActiefAsync(persistence.ResolveClubCode(), logger);
    public Task<(string onderwerp, string body)> BouwTemplateAntwoordAsync(InkomendBericht bericht,
        BerichtClassificatie classificatie, string plannerResponseJson, ILogger logger, TraceBuilder trace)
        => BerichtPipeline.BouwTemplateAntwoord(classificatie, plannerResponseJson, bericht, logger, trace: trace);
    public Task BewaarKorteTraceAsync(int verwerkingId, string verzoekType, BeslissingsTrace trace, ILogger logger)
        => persistence.UpsertTraceAsync(verwerkingId, verzoekType, trace, AppVersie);
    public Task BewaarTraceAsync(int verwerkingId, string verzoekType, BeslissingsTrace trace)
        => persistence.UpsertTraceAsync(verwerkingId, verzoekType, trace, AppVersie);
    public Task BewaarOnbekendeTeamtekstenAsync(BeslissingsTrace trace, int verwerkingId, ILogger logger)
        => OnbekendeTeamTekstOpslag.BewaarVeiligAsync(trace, persistence.ResolveClubCode(), verwerkingId,
            new Admin.SqlOnbekendeTeamTekstStore(SystemUtilities.DatabaseConfig.ConnectionString), logger);
    public Task StuurTeamleiderNotificatieAsync(BerichtClassificatie classificatie, ILogger logger)
        => EmailProcessorFunction.StuurTeamleiderNotificatieAsync(graph, classificatie.TeamNaam!, classificatie.Datum!, logger);

    internal static string Body(InkomendBericht bericht) => bericht.Body;
}
