using Microsoft.Extensions.Logging;
using Planner.Shared.Email;

namespace Planner.Endpoints.Email;

/// <summary>Voert de tier-onafhankelijke batchregels voor AI-classificatie uit.</summary>
public static class EmailClassificationBatchProcessor
{
    /// <summary>
    /// Classificeert berichten sequentieel. Een quota-fout stopt de batch; andere fouten tellen
    /// mee en laten de volgende berichten doorgaan. De tier levert berichtvelden en datumvalidatie.
    /// </summary>
    public static async Task<EmailClassificatieBatch<TBericht>> ClassificeerBatchAsync<TBericht>(
        IEnumerable<TBericht> emails,
        Func<TBericht, IReadOnlyList<ClassificatieCorrectieVoorbeeld>, Task<BerichtClassificatie>> classifyAsync,
        Func<Exception, bool> isQuotaFout,
        Func<TBericht, string> body,
        Func<TBericht, string> onderwerp,
        Action<BerichtClassificatie, string, string> valideerDagDatum,
        ILogger log,
        IReadOnlyList<ClassificatieCorrectieVoorbeeld>? voorbeelden = null)
        where TBericht : IInkomendBericht
    {
        var classificaties = new List<(TBericht Email, BerichtClassificatie Classificatie)>();
        var nietQuotaFouten = 0;

        foreach (var email in emails)
        {
            try
            {
                var classificatie = await classifyAsync(email, voorbeelden ?? []);
                valideerDagDatum(classificatie, body(email), onderwerp(email));
                classificaties.Add((email, classificatie));
            }
            catch (Exception ex) when (isQuotaFout(ex))
            {
                log.LogError(ex, "OpenAI quota overschreden — email processor stopt voor deze batch");
                return new EmailClassificatieBatch<TBericht>(
                    classificaties, AiAborted: true, QuotaException: ex, NietQuotaClassificatieFouten: nietQuotaFouten);
            }
            catch (Exception ex)
            {
                nietQuotaFouten++;
                log.LogError(ex, "AI-classificatie mislukt voor email {MessageId} — blijft ongelezen voor volgende poll",
                    email.MessageId);
            }
        }

        return new EmailClassificatieBatch<TBericht>(
            classificaties, AiAborted: false, QuotaException: null, NietQuotaClassificatieFouten: nietQuotaFouten);
    }
}
