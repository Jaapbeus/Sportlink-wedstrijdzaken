using Microsoft.Extensions.Logging;
using FunctionApp.Postgres.Processing;
using Planner.Endpoints.Email;
using Planner.Shared.Email;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/EmailClassificationService.cs</c> (#972 —
/// port van EmailProcessorFunction). Woordelijke kopie.
/// </summary>
internal sealed class EmailClassificationService
{
    internal async Task<EmailClassificatieBatch<InkomendBericht>> ClassificeerBatchAsync(
        IEnumerable<InkomendBericht> emails,
        Func<InkomendBericht, IReadOnlyList<ClassificatieCorrectieVoorbeeld>, Task<BerichtClassificatie>> classifyAsync,
        Func<Exception, bool> isQuotaFout,
        ILogger log,
        IReadOnlyList<ClassificatieCorrectieVoorbeeld>? voorbeelden = null)
    {
        var classificaties = new List<(InkomendBericht Email, BerichtClassificatie Classificatie)>();
        var nietQuotaFouten = 0;

        foreach (var email in emails)
        {
            try
            {
                var classificatie = await classifyAsync(email, voorbeelden ?? []);
                BerichtPipeline.ValideerDagDatum(classificatie, email.Body ?? "", email.Onderwerp);
                classificaties.Add((email, classificatie));
            }
            catch (Exception ex) when (isQuotaFout(ex))
            {
                log.LogError(ex, "OpenAI quota overschreden — email processor stopt voor deze batch");
                return new EmailClassificatieBatch<InkomendBericht>(
                    classificaties, AiAborted: true, QuotaException: ex, NietQuotaClassificatieFouten: nietQuotaFouten);
            }
            catch (Exception ex)
            {
                nietQuotaFouten++;
                log.LogError(ex, "AI-classificatie mislukt voor email {MessageId} — blijft ongelezen voor volgende poll", email.MessageId);
            }
        }

        return new EmailClassificatieBatch<InkomendBericht>(
            classificaties, AiAborted: false, QuotaException: null, NietQuotaClassificatieFouten: nietQuotaFouten);
    }
}
