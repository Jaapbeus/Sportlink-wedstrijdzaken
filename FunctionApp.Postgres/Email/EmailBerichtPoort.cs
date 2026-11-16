using Microsoft.Extensions.Logging;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using Planner.Shared.TeamResolution;
using Planner.Shared;
using FunctionApp.Postgres.Admin;
using FunctionApp.Postgres.Processing;

namespace FunctionApp.Postgres.Email;

internal sealed class EmailBerichtPoort(
    string cs, string clubCode, IEmailGraphService graph, BerichtAiService ai,
    EmailReplyPolicyService replyPolicy, ITeamResolver teamResolver,
    EmailVerwerkingsInstellingen? emailInstellingen = null)
    : IEmailBerichtPoort<InkomendBericht>
{
    private string AppVersie => EmailTraceRecord.VersieVan(typeof(EmailProcessorFunction).Assembly);
    public IReplyGraph Graph => graph;
    public EmailVerwerkingsInstellingen EmailInstellingen => emailInstellingen ?? EmailVerwerkingsInstellingen.VeiligeStandaard;
    public IReplyPersistentie Persistentie => new SqlReplyPersistentie(cs);
    public Func<string, string> SanitizeFoutMelding => EmailSanitizer.SanitizeFoutMelding;
    public string MessageId(InkomendBericht bericht) => bericht.MessageId;
    public string Afzender(InkomendBericht bericht) => bericht.Afzender;
    public string? ConversationId(InkomendBericht bericht) => bericht.ConversationId;
    public Task MarkeerGelezenAsync(string messageId) => graph.MarkAsReadAsync(messageId);
    public Task<int?> BepaalVerwerkingIdAsync(InkomendBericht bericht, ILogger log)
        => EmailVerwerkingKoppeling.BepaalVerwerkingIdAsync(cs, clubCode, bericht, graph, log);
    public Task<(bool IsReply, int? OrigineelId, string? OrigineelType, string? OrigineleSamenvatting)> DetecteerReplyAsync(string conversationId, ILogger log)
        => SqlEmailPersistenceRepository.DetecteerReplyOpOnsAntwoordAsync(cs, conversationId, clubCode, log);
    public Task UpdateReplyStatusAsync(int verwerkingId, int origineelId)
        => SqlEmailPersistenceRepository.UpdateReplyStatusAsync(cs, verwerkingId, true, origineelId);
    public Task<(bool IsCorrectie, string? JuistType, string? Samenvatting)> DetecteerCorrectieAsync(InkomendBericht bericht, string origineelType, string? origineleSamenvatting)
        => ai.DetecteerCorrectieAsync(Body(bericht), bericht.Onderwerp, origineelType, origineleSamenvatting);
    public Task BewaarCorrectieAsync(int origineelId, int verwerkingId, string origineelType, string? juistType,
        string? origineleSamenvatting, string? samenvatting)
        => LearningMomentRepository.InsertClassificatieCorrectieAsync(cs, origineelId, verwerkingId,
            origineelType, juistType, origineleSamenvatting, samenvatting, clubCode);
    public Task<bool> HandelBuitenScopeAsync(int verwerkingId, InkomendBericht bericht,
        BerichtClassificatie classificatie, string classificatieJson, ILogger log)
        => EmailVerwerkingKoppeling.HandelBuitenScopeAsync(cs, verwerkingId, bericht.MessageId,
            classificatie.Type == VerzoekType.BuitenScope, classificatieJson, graph, log);
    public Task<bool> HandelAntwoordLimietAsync(int verwerkingId, InkomendBericht bericht, BerichtClassificatie classificatie, string classificatieJson, ILogger log)
        => EmailVerwerkingKoppeling.HandelAntwoordLimietAsync(cs, clubCode, verwerkingId, bericht, classificatie, classificatieJson, graph, log);
    public Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? classificatieJson)
        => SqlEmailPersistenceRepository.UpdateStatusAsync(cs, verwerkingId, status, classificatieJson);
    public Task<string> VerwerkMetPlannerAsync(InkomendBericht bericht, BerichtClassificatie classificatie, TraceBuilder trace, ILogger log)
        => BerichtPipeline.VerwerkMetPlannerAsync(classificatie, bericht, log, teamResolver, clubCode, trace: trace);
    public Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson)
        => SqlEmailPersistenceRepository.UpdatePlannerResponseAsync(cs, verwerkingId, plannerResponseJson);
    public Task<ReplyVerwerkingUitkomst> HandelReplyFlowAfAsync(int verwerkingId, InkomendBericht bericht,
        BerichtClassificatie classificatie, string plannerResponseJson, bool reviewMode, string? reviewRecipient,
        Func<Task<(string onderwerp, string body)>> bouwTemplateAsync, Func<string, string> sanitizeFoutMelding,
        ILogger log, TraceBuilder trace, bool zekerheidspoortActief)
        => replyPolicy.HandelReplyFlowAfAsync(cs, verwerkingId, bericht, classificatie, plannerResponseJson,
            reviewMode, reviewRecipient, graph, bouwTemplateAsync, sanitizeFoutMelding, log, trace,
            zekerheidspoortActief, clubCode);
    public Task<bool> ZekerheidspoortActiefAsync(ILogger log)
        => ZekerheidspoortInstelling.IsActiefAsync(cs, clubCode, log);
    public Task<(string onderwerp, string body)> BouwTemplateAntwoordAsync(InkomendBericht bericht,
        BerichtClassificatie classificatie, string plannerResponseJson, ILogger log, TraceBuilder trace)
        => BerichtPipeline.BouwTemplateAntwoord(classificatie, plannerResponseJson,
            bericht, log, null, clubCode, trace);
    public Task BewaarKorteTraceAsync(int verwerkingId, string verzoekType, BeslissingsTrace trace, ILogger log)
        => EmailTraceRepository.UpsertAsync(cs, EmailTraceRecord.Van(verwerkingId, clubCode,
            verzoekType, trace, AppVersie));
    public Task BewaarTraceAsync(int verwerkingId, string verzoekType, BeslissingsTrace trace)
        => EmailTraceRepository.UpsertAsync(cs, EmailTraceRecord.Van(verwerkingId, clubCode,
            verzoekType, trace, AppVersie));
    public Task BewaarOnbekendeTeamtekstenAsync(BeslissingsTrace trace, int verwerkingId, ILogger log)
        => OnbekendeTeamTekstOpslag.BewaarVeiligAsync(trace, clubCode, verwerkingId,
            new PostgresOnbekendeTeamTekstStore(cs), log);
    public Task StuurTeamleiderNotificatieAsync(BerichtClassificatie classificatie, ILogger log)
        => EmailProcessorFunction.StuurTeamleiderNotificatieAsync(
            cs, clubCode, graph, classificatie.TeamNaam!, classificatie.Datum!, log);

    internal static string Body(InkomendBericht bericht) => bericht.Body ?? "";
}
