using System.Data.Common;
using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace Planner.Shared.Sync;

/// <summary>Een job die het herstel opnieuw op de queue moet zetten.</summary>
public sealed record SyncJobRecoveryItem(Guid JobId, string ClubCode, int WeekOffsetFrom, int WeekOffsetTo);

/// <summary>Uitkomst van één databasestap van het herstel: wat is opnieuw aangeboden en wat opgegeven.</summary>
public sealed record SyncJobRecoveryBatch(IReadOnlyList<SyncJobRecoveryItem> Retry, int GaveUp);

/// <summary>
/// Orkestratie van het herstel van onderbroken sync-jobs (#1839), gedeeld door beide tiers: de
/// database beslist atomair (UPDATE met drempel uit <see cref="SyncJobRecoveryPolicy"/>), daarna
/// zet deze klasse elke herplaatste job opnieuw op de queue. Een mislukte enqueue laat de job op
/// "pending" staan; na dezelfde termijn pakt het herstel hem opnieuw op (zonder poging te
/// verbruiken, de claim telt de poging), dus de herhalingen blijven begrensd.
/// </summary>
public static class SyncJobRecoveryCore
{
    public static async Task<(int Requeued, int EnqueueFailed, int GaveUp)> RunAsync(
        DateTime utcNow,
        Func<DateTime, Task<SyncJobRecoveryBatch>> recoverAsync,
        Func<SyncJobRecoveryItem, Task> enqueueAsync,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(recoverAsync);
        ArgumentNullException.ThrowIfNull(enqueueAsync);
        ArgumentNullException.ThrowIfNull(log);

        var batch = await recoverAsync(SyncJobRecoveryPolicy.StaleCutoff(utcNow));
        var requeued = 0;
        var failed = 0;
        foreach (var item in batch.Retry)
        {
            try
            {
                await enqueueAsync(item);
                requeued++;
                log.LogWarning("SyncJobRecovery: onderbroken job {JobId} opnieuw op de queue gezet", item.JobId);
            }
            catch (Exception ex)
            {
                failed++;
                log.LogError("SyncJobRecovery: job {JobId} terug op pending maar enqueue mislukt ({ExceptionType}); volgende ronde probeert opnieuw", item.JobId, ex.GetType().Name);
            }
        }

        if (batch.GaveUp > 0)
            log.LogWarning("SyncJobRecovery: {Count} onderbroken job(s) opgegeven na {Max} pogingen", batch.GaveUp, SyncJobRecoveryPolicy.MaxAttempts);

        return (requeued, failed, batch.GaveUp);
    }

    /// <summary>
    /// Herstel dat meeloopt met bestaande activiteit (een sync starten, de status opvragen, de
    /// geplande sync; eigenaarsbesluit #1839: geen eigen timer, dus geen extra database-wake-ups):
    /// zelfde als <see cref="RunAsync(DateTime, Func{DateTime, Task{SyncJobRecoveryBatch}}, Func{SyncJobRecoveryItem, Task}, ILogger)"/>
    /// met de enqueue op de bestaande sync-jobs-queue. Het bericht heeft dezelfde vorm als dat van
    /// <c>AdminSyncFunction.Trigger</c> (<c>JobId</c>, <c>ClubCode</c>, <c>WeekOffsetFrom</c>,
    /// <c>WeekOffsetTo</c>). Een fout hier mag de aanroepende activiteit nooit breken: ze wordt
    /// alleen gelogd (zonder uitzonderingsobject) en de volgende activiteit probeert het opnieuw.
    /// </summary>
    public static async Task TryRunAsync(
        Func<DateTime, Task<SyncJobRecoveryBatch>> recoverAsync,
        Func<QueueClient> createQueueClient,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(createQueueClient);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            var queueClient = createQueueClient();
            await queueClient.CreateIfNotExistsAsync();
            await RunAsync(DateTime.UtcNow, recoverAsync,
                item => queueClient.SendMessageAsync(JsonSerializer.Serialize(item)), log);
        }
        catch (Exception ex)
        {
            log.LogError("SyncJobRecovery: herstel overgeslagen ({ExceptionType}); de volgende activiteit probeert opnieuw", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Leest de rijen die de ene atomaire herstel-UPDATE teruggeeft (kolommen: id, clubcode,
    /// weekoffsetfrom, weekoffsetto, nieuwe status): "pending" is opnieuw aan te bieden, "failed"
    /// is opgegeven.
    /// </summary>
    public static async Task<SyncJobRecoveryBatch> ReadBatchAsync(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var retry = new List<SyncJobRecoveryItem>();
        var gaveUp = 0;
        while (await reader.ReadAsync())
        {
            if (reader.GetString(4) == "failed")
                gaveUp++;
            else
                retry.Add(new SyncJobRecoveryItem(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        return new SyncJobRecoveryBatch(retry, gaveUp);
    }
}
