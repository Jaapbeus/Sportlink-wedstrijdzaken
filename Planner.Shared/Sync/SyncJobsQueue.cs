using Azure.Storage.Queues;

namespace Planner.Shared.Sync;

/// <summary>One queue name and base64 client for sync producers and consumers in both tiers.</summary>
public static class SyncJobsQueue
{
    public const string QueueName = "sync-jobs";

    /// <summary>The Functions queue trigger expects base64 encoded messages.</summary>
    public static QueueClient CreateClient() =>
        Infrastructure.OpslagVerbinding.MaakQueueClient(
            QueueName, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
}
