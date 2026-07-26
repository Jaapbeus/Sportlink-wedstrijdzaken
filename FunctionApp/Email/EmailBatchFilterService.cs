using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

internal sealed class EmailBatchFilterService
{
    internal async Task<List<InkomendBericht>> PreFilterVoorClassificatieAsync(
        IEnumerable<InkomendBericht> emails,
        string eigenMailbox,
        IReadOnlySet<string> uitgeslotenCache,
        IEmailGraphService graphService,
        ILogger log)
    {
        var teClassificeren = new List<InkomendBericht>();

        foreach (var email in emails)
        {
            if (email.Afzender.Equals(eigenMailbox, StringComparison.OrdinalIgnoreCase))
            {
                log.LogInformation("Email {MessageId} is van eigen mailbox, overslaan", email.MessageId);
                await graphService.MarkAsReadAsync(email.MessageId);
            }
            else if (uitgeslotenCache.Contains(email.Afzender))
            {
                log.LogInformation("Email {MessageId} van uitgesloten adres (cache), overslaan (afzender niet gelogd — AVG #210)", email.MessageId);
                await graphService.MarkAsReadAsync(email.MessageId);
            }
            else
            {
                teClassificeren.Add(email);
            }
        }

        return teClassificeren;
    }

    internal List<InkomendBericht> FilterUitgeslotenAdressen(
        IEnumerable<InkomendBericht> emails,
        IReadOnlySet<string> uitgeslotenAdressen)
        => emails.Where(e => !uitgeslotenAdressen.Contains(e.Afzender)).ToList();

    internal async Task LabelBuitenScopeAsync(
        IEnumerable<(InkomendBericht Email, BerichtClassificatie Classificatie)> classificaties,
        IEmailGraphService graphService,
        ILogger log)
    {
        foreach (var (email, _) in classificaties.Where(c => c.Classificatie.Type == VerzoekType.BuitenScope))
        {
            try
            {
                await graphService.EnsureMasterCategoryAsync("Geen AI antwoord", "preset0");
                await graphService.SetCategoriesAsync(email.MessageId, "Geen AI antwoord");
                await graphService.MarkAsReadAsync(email.MessageId);
                log.LogInformation("Email {MessageId} buiten scope — gelabeld in Outlook, database slaapt", email.MessageId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Fout bij Outlook-labeling BuitenScope email {MessageId}", email.MessageId);
            }
        }
    }
}
