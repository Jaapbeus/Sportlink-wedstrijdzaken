using Microsoft.Extensions.Logging;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.TeamResolution;

namespace FunctionApp.Postgres.Email;

public partial class EmailProcessorFunction
{
    internal static Task VerwerkEmailAsync(
        string cs, string clubCode, InkomendBericht email, BerichtClassificatie classificatie,
        IEmailGraphService graphService, IReadOnlySet<string> uitgeslotenAdressen,
        BerichtAiService aiService, EmailReplyPolicyService replyPolicyService,
        ILogger log, ITeamResolver teamResolver, EmailVerwerkingsInstellingen? emailInstellingen = null)
        => EmailBerichtVerwerking.VerwerkAsync(email, classificatie, uitgeslotenAdressen, log,
            new EmailBerichtPoort(cs, clubCode, graphService, aiService, replyPolicyService, teamResolver, emailInstellingen));

    internal static Task<int?> BepaalVerwerkingIdAsync(
        string cs, string clubCode, InkomendBericht email, IEmailGraphService graphService, ILogger log)
        => EmailVerwerkingKoppeling.BepaalVerwerkingIdAsync(cs, clubCode, email, graphService, log);

    internal static Task<bool> HandelBuitenScopeAsync(
        string cs, int verwerkingId, string messageId, BerichtClassificatie classificatie,
        string classificatieJson, IEmailGraphService graphService, ILogger log)
        => EmailVerwerkingKoppeling.HandelBuitenScopeAsync(cs, verwerkingId, messageId,
            classificatie.Type == VerzoekType.BuitenScope, classificatieJson, graphService, log);
}
