using Microsoft.Extensions.Logging;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.Email.Trace;
using Planner.Shared.TeamResolution;

namespace SportlinkFunction.Email;

public partial class EmailProcessorFunction
{
    internal static Task VerwerkEmailAsync(
        InkomendBericht email, BerichtClassificatie classificatie, IEmailGraphService graphService,
        IReadOnlySet<string> uitgeslotenAdressen, BerichtAiService aiService,
        IEmailPersistenceService persistenceService, EmailReplyPolicyService replyPolicyService,
        ILogger log, ITeamResolver teamResolver)
        => EmailBerichtVerwerking.VerwerkAsync(email, classificatie, uitgeslotenAdressen, log,
            new EmailBerichtPoort(graphService, persistenceService, aiService, replyPolicyService, teamResolver));

    internal static Task BewaarTraceVeiligAsync(
        IEmailPersistenceService persistenceService, int verwerkingId, string verzoekType,
        BeslissingsTrace trace, ILogger log)
        => EmailTraceOpslag.BewaarVeiligAsync(
            () => persistenceService.UpsertTraceAsync(verwerkingId, verzoekType, trace,
                EmailTraceRecord.VersieVan(typeof(EmailProcessorFunction).Assembly)), log, verwerkingId);

    internal static Task<int?> BepaalVerwerkingIdAsync(
        InkomendBericht email, IEmailGraphService graphService,
        IEmailPersistenceService persistenceService, ILogger log)
        => EmailVerwerkingKoppeling.BepaalVerwerkingIdAsync(email, graphService, persistenceService, log);

    internal static Task<bool> HandelBuitenScopeAsync(
        int verwerkingId, string messageId, BerichtClassificatie classificatie, string classificatieJson,
        IEmailGraphService graphService, IEmailPersistenceService persistenceService, ILogger log)
        => EmailVerwerkingKoppeling.HandelBuitenScopeAsync(verwerkingId, messageId,
            classificatie.Type == VerzoekType.BuitenScope, classificatieJson, graphService, persistenceService, log);
}
