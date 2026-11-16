using Microsoft.Extensions.Logging;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;

namespace Planner.Endpoints.Email;

/// <summary>Tier-adapter voor de volledige afhandeling van één geclassificeerd bericht.</summary>
internal interface IEmailBerichtPoort<TBericht>
{
    string MessageId(TBericht bericht);
    string Afzender(TBericht bericht);
    string? ConversationId(TBericht bericht);
    IReplyGraph Graph { get; }
    EmailVerwerkingsInstellingen EmailInstellingen => EmailVerwerkingsInstellingen.VeiligeStandaard;
    IReplyPersistentie Persistentie { get; }
    Task MarkeerGelezenAsync(string messageId);
    Task<int?> BepaalVerwerkingIdAsync(TBericht bericht, ILogger log);
    Task<(bool IsReply, int? OrigineelId, string? OrigineelType, string? OrigineleSamenvatting)> DetecteerReplyAsync(
        string conversationId, ILogger log);
    Task UpdateReplyStatusAsync(int verwerkingId, int origineelId);
    Task<(bool IsCorrectie, string? JuistType, string? Samenvatting)> DetecteerCorrectieAsync(
        TBericht bericht, string origineelType, string? origineleSamenvatting);
    Task BewaarCorrectieAsync(int origineelId, int verwerkingId, string origineelType,
        string? juistType, string? origineleSamenvatting, string? samenvatting);
    Task<bool> HandelBuitenScopeAsync(int verwerkingId, TBericht bericht,
        BerichtClassificatie classificatie, string classificatieJson, ILogger log);
    Task<bool> HandelAntwoordLimietAsync(int verwerkingId, TBericht bericht,
        BerichtClassificatie classificatie, string classificatieJson, ILogger log);
    Task UpdateStatusAsync(int verwerkingId, EmailStatus status, string? classificatieJson);
    Task<string> VerwerkMetPlannerAsync(TBericht bericht, BerichtClassificatie classificatie,
        TraceBuilder trace, ILogger log);
    Task UpdatePlannerResponseAsync(int verwerkingId, string plannerResponseJson);
    Task<ReplyVerwerkingUitkomst> HandelReplyFlowAfAsync(int verwerkingId, TBericht bericht,
        BerichtClassificatie classificatie, string plannerResponseJson, bool reviewMode,
        string? reviewRecipient, Func<Task<(string onderwerp, string body)>> bouwTemplateAsync,
        Func<string, string> sanitizeFoutMelding, ILogger log, TraceBuilder trace,
        bool zekerheidspoortActief);
    Task<bool> ZekerheidspoortActiefAsync(ILogger log);
    Task<(string onderwerp, string body)> BouwTemplateAntwoordAsync(TBericht bericht,
        BerichtClassificatie classificatie, string plannerResponseJson, ILogger log, TraceBuilder trace);
    Task BewaarKorteTraceAsync(int verwerkingId, string verzoekType, BeslissingsTrace trace, ILogger log);
    Task BewaarTraceAsync(int verwerkingId, string verzoekType, BeslissingsTrace trace);
    Task BewaarOnbekendeTeamtekstenAsync(BeslissingsTrace trace, int verwerkingId, ILogger log);
    Task StuurTeamleiderNotificatieAsync(BerichtClassificatie classificatie, ILogger log);
    Func<string, string> SanitizeFoutMelding { get; }
}
