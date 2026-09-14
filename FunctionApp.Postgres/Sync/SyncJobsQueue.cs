using Azure.Storage.Queues;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Naam en client-fabriek van de Azure Storage Queue die <c>AdminSyncFunction.Trigger</c> gebruikt
/// om een sync-job te laten verwerken door <see cref="SyncJobProcessor"/> (#1138). Gedeelde
/// constante zodat de producer (HTTP-trigger) en de consumer (QueueTrigger, via het
/// <c>[QueueTrigger]</c>-attribuut) niet onafhankelijk uit de pas kunnen lopen op de queue-naam.
/// </summary>
internal static class SyncJobsQueue
{
    internal const string QueueName = "sync-jobs";

    /// <summary>
    /// <c>[QueueTrigger]</c> verwacht standaard base64-gecodeerde berichten (host.json
    /// <c>messageEncoding</c>, default <c>base64</c>) — de kale <see cref="QueueClient"/> codeert
    /// standaard NIET. Zonder deze optie faalt de trigger-extensie stil op elk bericht: de job
    /// blijft voor altijd op <c>pending</c> staan omdat <see cref="SyncJobProcessor"/> nooit wordt
    /// aangeroepen. Één fabrieksmethode zodat dit nooit los van elkaar kan gaan afwijken.
    /// </summary>
    internal static QueueClient CreateClient(string connectionString)
        => new(connectionString, QueueName, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
}
