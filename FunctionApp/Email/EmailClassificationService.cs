using Microsoft.Extensions.Logging;
using SportlinkFunction.Processing;

namespace SportlinkFunction.Email;

internal sealed record EmailClassificationBatchResult(
    List<(InkomendBericht Email, BerichtClassificatie Classificatie)> Classificaties,
    bool AiAborted,
    Exception? QuotaException);

internal sealed class EmailClassificationService
{
    internal async Task<EmailClassificationBatchResult> ClassificeerBatchAsync(
        IEnumerable<InkomendBericht> emails,
        Func<InkomendBericht, Task<BerichtClassificatie>> classifyAsync,
        Func<Exception, bool> isQuotaFout,
        ILogger log)
    {
        var classificaties = new List<(InkomendBericht Email, BerichtClassificatie Classificatie)>();

        foreach (var email in emails)
        {
            try
            {
                var classificatie = await classifyAsync(email);
                BerichtPipeline.ValideerDagDatum(classificatie, email.Body, email.Onderwerp);
                classificaties.Add((email, classificatie));
            }
            catch (Exception ex) when (isQuotaFout(ex))
            {
                log.LogError(ex, "OpenAI quota overschreden — email processor stopt voor deze batch");
                return new EmailClassificationBatchResult(classificaties, AiAborted: true, QuotaException: ex);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "AI-classificatie mislukt voor email {MessageId} — blijft ongelezen voor volgende poll", email.MessageId);
            }
        }

        return new EmailClassificationBatchResult(classificaties, AiAborted: false, QuotaException: null);
    }
}
