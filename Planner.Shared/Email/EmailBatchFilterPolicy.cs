using Microsoft.Extensions.Logging;

namespace Planner.Shared.Email;

/// <summary>Tier-onafhankelijke beslissing voor het voorfilter van de e-mailbatch.</summary>
public static class EmailBatchFilterPolicy
{
    private const string NoodmailOnderwerpPrefix = "URGENT:";

    /// <summary>
    /// Handelt een bericht van de eigen mailbox af. Een eigen noodmail blijft ongelezen; andere
    /// eigen berichten worden gelezen gemarkeerd. De onderwerpherkenning is alleen een extra
    /// voorwaarde naast de exacte afzendercontrole, omdat onderwerpen zelf geen afzender bewijzen.
    /// </summary>
    /// <returns><c>true</c> als het bericht van de eigen mailbox komt en dus niet geclassificeerd mag worden.</returns>
    public static async Task<bool> HandelEigenMailboxBerichtAsync(
        IInkomendBericht email, string eigenMailbox, IEmailMailboxActies mailbox, ILogger log)
    {
        if (!email.Afzender.Equals(eigenMailbox, StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsNoodmailOnderwerp(email.Onderwerp))
        {
            log.LogInformation("Email {MessageId} is een noodmelding van eigen mailbox, overslaan en ongelezen laten", email.MessageId);
            return true;
        }

        log.LogInformation("Email {MessageId} is van eigen mailbox, overslaan", email.MessageId);
        await mailbox.MarkeerGelezenAsync(email.MessageId);
        return true;
    }

    /// <summary>Herkent alleen het vaste noodmail-onderwerpvoorvoegsel, hoofdletterongevoelig en zonder witruimte-normalisatie.</summary>
    public static bool IsNoodmailOnderwerp(string? onderwerp)
        => onderwerp?.StartsWith(NoodmailOnderwerpPrefix, StringComparison.OrdinalIgnoreCase) == true;
}
