using Microsoft.Extensions.Logging;
using Planner.Shared.Email;

namespace SportlinkFunction.Email;

internal sealed class EmailBatchFilterService
{
    internal Task<List<InkomendBericht>> PreFilterVoorClassificatieAsync(
        IEnumerable<InkomendBericht> emails,
        string eigenMailbox,
        IReadOnlySet<string> uitgeslotenCache,
        IEmailGraphService graphService,
        ILogger log)
        => EmailBatchFilterKern.PreFilterVoorClassificatieAsync(
            emails, eigenMailbox, uitgeslotenCache, email => email.InternetHeaders,
            graphService, new EmailMailboxAdapter(graphService), log);

    internal List<InkomendBericht> FilterUitgeslotenAdressen(
        IEnumerable<InkomendBericht> emails,
        IReadOnlySet<string> uitgeslotenAdressen)
        => EmailBatchFilterKern.FilterUitgeslotenAdressen(emails, uitgeslotenAdressen, email => email.Afzender);

    internal Task LabelBuitenScopeAsync(
        IEnumerable<(InkomendBericht Email, BerichtClassificatie Classificatie)> classificaties,
        IEmailGraphService graphService,
        ILogger log)
        => EmailBatchFilterKern.LabelBuitenScopeAsync(classificaties, email => email.MessageId, graphService, log);
}
