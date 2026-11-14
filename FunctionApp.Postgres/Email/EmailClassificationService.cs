using Microsoft.Extensions.Logging;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using FunctionApp.Postgres.Processing;

namespace FunctionApp.Postgres.Email;

/// <summary>Tier-adapter voor de gedeelde batchregels; behoudt de nullable body als lege tekst.</summary>
internal sealed class EmailClassificationService
{
    internal Task<EmailClassificatieBatch<InkomendBericht>> ClassificeerBatchAsync(
        IEnumerable<InkomendBericht> emails,
        Func<InkomendBericht, IReadOnlyList<ClassificatieCorrectieVoorbeeld>, Task<BerichtClassificatie>> classifyAsync,
        Func<Exception, bool> isQuotaFout,
        ILogger log,
        IReadOnlyList<ClassificatieCorrectieVoorbeeld>? voorbeelden = null)
        => EmailClassificationBatchProcessor.ClassificeerBatchAsync(
            emails, classifyAsync, isQuotaFout, email => email.Body ?? "", email => email.Onderwerp,
            BerichtPipeline.ValideerDagDatum, log, voorbeelden);
}
