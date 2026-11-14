using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>Tier-onafhankelijke filterlogica voor batches inkomende e-mail (#1716 slice 11).</summary>
public static class EmailBatchFilterKern
{
    public static async Task<List<TBericht>> PreFilterVoorClassificatieAsync<TBericht>(
        IEnumerable<TBericht> emails,
        string eigenMailbox,
        IReadOnlySet<string> uitgeslotenCache,
        Func<TBericht, IEnumerable<KeyValuePair<string, string>>?> haalInternetHeaders,
        IReplyGraph graph,
        IEmailMailboxActies mailbox,
        ILogger log)
        where TBericht : IInkomendBericht
    {
        var teClassificeren = new List<TBericht>();

        foreach (var email in emails)
        {
            if (email.Afzender.Equals(eigenMailbox, StringComparison.OrdinalIgnoreCase))
            {
                log.LogInformation("Email {MessageId} is van eigen mailbox, overslaan", email.MessageId);
                await EmailVerwerkingsAfhandeling.MarkeerGefilterdBerichtGelezenAsync(email.MessageId, graph, log);
            }
            else if (uitgeslotenCache.Contains(email.Afzender))
            {
                log.LogInformation("Email {MessageId} van uitgesloten adres (cache), overslaan (afzender niet gelogd — AVG #210)", email.MessageId);
                await EmailVerwerkingsAfhandeling.MarkeerGefilterdBerichtGelezenAsync(email.MessageId, graph, log);
            }
            else
            {
                var headers = haalInternetHeaders(email);
                if (AutomatischeAfzenderHerkenning.IsAutomatisch(headers))
                {
                    await EmailVerwerkingsAfhandeling.HandelAutomatischeAfzenderAsync(
                        email.MessageId, headers, mailbox, log);
                }
                else
                {
                    teClassificeren.Add(email);
                }
            }
        }

        return teClassificeren;
    }

    public static List<TBericht> FilterUitgeslotenAdressen<TBericht>(
        IEnumerable<TBericht> emails,
        IReadOnlySet<string> uitgeslotenAdressen,
        Func<TBericht, string> haalAfzender)
        => emails.Where(email => !uitgeslotenAdressen.Contains(haalAfzender(email))).ToList();

    public static async Task LabelBuitenScopeAsync<TBericht>(
        IEnumerable<(TBericht Email, BerichtClassificatie Classificatie)> classificaties,
        Func<TBericht, string> haalMessageId,
        IReplyGraph graph,
        ILogger log)
    {
        foreach (var (email, _) in classificaties.Where(c => c.Classificatie.Type == VerzoekType.BuitenScope))
        {
            var messageId = haalMessageId(email);
            try
            {
                await graph.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
                await graph.SetCategoriesAsync(messageId, EmailCategorieLabels.GeenAiAntwoord);
                await graph.MarkAsReadAsync(messageId);
                log.LogInformation("Email {MessageId} buiten scope — gelabeld in Outlook, database slaapt", messageId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Fout bij Outlook-labeling BuitenScope email {MessageId}", messageId);
            }
        }
    }
}
