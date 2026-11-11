using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace SportlinkFunction.Email;

/// <summary>Graph-specifieke mailboxmutaties, met dezelfde foutgrens voor beide hosts.</summary>
internal static class GraphMailboxMutations
{
    public static async Task SetCategoriesAsync(GraphServiceClient client, ILogger logger,
        string mailbox, string messageId, string[] categories)
    {
        try
        {
            await client.Users[mailbox].Messages[messageId]
                .PatchAsync(new Message { Categories = categories.ToList() });

            logger.LogInformation("Email {MessageId} gemarkeerd met categorie(en) {Categorieen}",
                messageId, string.Join(", ", categories));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fout bij zetten van categorie op email {MessageId}", messageId);
            throw;
        }
    }

    public static async Task MarkAsReadAsync(GraphServiceClient client, ILogger logger,
        string mailbox, string messageId)
    {
        try
        {
            await client.Users[mailbox].Messages[messageId]
                .PatchAsync(new Message { IsRead = true });

            logger.LogInformation("Email {MessageId} gemarkeerd als gelezen", messageId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fout bij markeren van email {MessageId} als gelezen", messageId);
            throw;
        }
    }
}
