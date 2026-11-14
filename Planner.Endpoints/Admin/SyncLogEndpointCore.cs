using Microsoft.AspNetCore.Mvc;
using Azure.Storage.Queues;
using System.Text.Json;
using Planner.Shared.Sync;

namespace Planner.Endpoints.Admin;

/// <summary>Veilige, gedeelde weergave en queue-orkestratie van het sync-logboek.</summary>
public static class SyncLogEndpointCore
{
    public static async Task<IActionResult> StartAsync(string? body, int year,
        Func<Task<int>> seasonEnd, Func<int, Task<int?>> seasonStart, Func<Task> recover,
        Func<int, int, Task<SyncJobCreation>> create, Func<SyncJobStatus, Task> enqueue,
        Func<Guid, Task> markEnqueueFailed, Func<Guid, Task<IReadOnlyList<SyncLogEntry>>> readPhases)
    {
        var window = await SyncTriggerEndpointCore.BepaalVensterAsync(body, year, seasonEnd, seasonStart);
        if (window.Fout is not null) return window.Fout;
        await recover();
        var creation = await create(window.Van, window.Tot);
        return await TriggerAsync(creation, enqueue, () => markEnqueueFailed(creation.Job.Id), async () =>
        {
            var phases = await readPhases(creation.Job.Id);
            return phases.LastOrDefault(p => p.Attempt == creation.Job.Attempt && p.Status == "running")?.Phase;
        });
    }

    public static async Task EnqueueAsync(QueueClient client, SyncJobStatus job, string clubCode)
    {
        await client.CreateIfNotExistsAsync();
        await client.SendMessageAsync(JsonSerializer.Serialize(
            new SyncJobRecoveryItem(job.Id, clubCode, job.WeekOffsetFrom, job.WeekOffsetTo)));
    }

    public static async Task<IActionResult> ReadAsync(DateTime? lastSync, string schedule,
        IReadOnlyList<SyncJobStatus> jobs, Guid? requestedId, Func<Task<int>> cleanup,
        Func<Task<IReadOnlyList<SyncLogEntry>>> readEntries)
    {
        IReadOnlyList<SyncLogEntry> entries = Array.Empty<SyncLogEntry>();
        var available = true;
        try
        {
            await cleanup();
            entries = await readEntries();
        }
        catch { available = false; }
        return new OkObjectResult(Status(lastSync, schedule, jobs, entries, requestedId, DateTime.UtcNow, available));
    }

    public static async Task<IActionResult> TriggerAsync(SyncJobCreation creation,
        Func<SyncJobStatus, Task> enqueue, Func<Task> enqueueFailed, Func<Task<string?>> activePhase)
    {
        if (creation.Created)
        {
            try { await enqueue(creation.Job); }
            catch
            {
                try { await enqueueFailed(); }
                catch
                {
                    return new ObjectResult(new { error = "Synchronisatie kon niet worden ingepland. De status is tijdelijk onzeker; probeer het later opnieuw." }) { StatusCode = 503 };
                }
                return new ObjectResult(new { error = "Synchronisatie kon niet worden ingepland. Probeer opnieuw." }) { StatusCode = 503 };
            }
        }
        string? phase = null;
        try { phase = await activePhase(); }
        catch { /* Een ontbrekend logboek verandert het aanmaken van de job niet. */ }
        var job = creation.Job;
        return new ObjectResult(new
        {
            status = creation.Created ? "gestart" : "bezig",
            jobId = job.Id,
            weekOffsetFrom = job.WeekOffsetFrom,
            weekOffsetTo = job.WeekOffsetTo,
            tijdstip = job.CreatedAt,
            startedAt = job.StartedAt,
            phase,
            alreadyRunning = !creation.Created,
            melding = creation.Created ? "Synchronisatie ingepland." : "Er loopt al een synchronisatie."
        }) { StatusCode = 202 };
    }

    public static object Status(DateTime? lastSync, string schedule, IReadOnlyList<SyncJobStatus> jobs,
        IReadOnlyList<SyncLogEntry> entries, Guid? requestedId, DateTime utcNow, bool logAvailable)
    {
        var active = jobs.FirstOrDefault(j => IsActive(j, utcNow));
        var completed = jobs.Where(j => j.CompletedAt.HasValue).MaxBy(j => j.CompletedAt);
        var selected = requestedId.HasValue ? jobs.FirstOrDefault(j => j.Id == requestedId) : active ?? completed ?? jobs.FirstOrDefault();
        return new
        {
            lastSyncTimestamp = lastSync,
            fetchSchedule = schedule,
            status = lastSync.HasValue ? "ok" : "geen-sync-uitgevoerd",
            job = Map(selected, entries),
            activeJob = Map(active, entries),
            lastCompletedJob = Map(completed, entries),
            jobs = jobs.Take(10).Select(j => Map(j, entries)).ToArray(),
            logAvailable
        };
    }

    private static bool IsActive(SyncJobStatus job, DateTime now) => job.Status == "running" ||
        (job.Status == "pending" && (job.Attempt > 0 || job.CreatedAt > SyncJobRecoveryPolicy.StaleCutoff(now)));

    private static object? Map(SyncJobStatus? job, IReadOnlyList<SyncLogEntry> entries)
    {
        if (job is null) return null;
        var phases = entries.Where(e => e.JobId == job.Id && e.Attempt == job.Attempt).OrderBy(e => e.StartedAt).ToArray();
        var partial = phases.Any(e => e.Status == "partial" ||
            (e.Status == "skipped" && e.ReasonCode != "not_applicable")) ||
            (phases.Any(e => e.Status == "failed") && phases.Any(e => e.Status == "succeeded"));
        var status = job.Status == "succeeded" && job.CompletedAt.HasValue && partial ? "partial" : job.Status;
        return new
        {
            id = job.Id, status, weekOffsetFrom = job.WeekOffsetFrom, weekOffsetTo = job.WeekOffsetTo,
            createdAt = job.CreatedAt, startedAt = job.StartedAt, completedAt = job.CompletedAt,
            errorMessage = status == "failed" ? "Synchronisatie mislukt. Bekijk de fasen of probeer opnieuw." :
                status == "partial" ? "Synchronisatie onvolledig. Bekijk de reden per fase." : null,
            attempt = job.Attempt, phases
        };
    }
}
