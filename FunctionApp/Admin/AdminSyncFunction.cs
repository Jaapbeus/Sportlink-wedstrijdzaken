using Planner.Endpoints.Admin;
using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor synchronisatie. v2 — #89.
///
/// GET  /api/beheer/sync/status   → laatste sync timestamp + huidige FetchSchedule + status van de
///                                   meest recente (of opgevraagde) sync-job
/// POST /api/beheer/sync/trigger  → zet een sync-job op de queue (#1138, #415)
///
/// <b>Trigger</b> is niet langer fire-and-forget via <c>Task.Run</c>: de HTTP-call schrijft een
/// <c>dbo.SyncJobs</c>-rij (status <c>pending</c>) en zet een bericht op de
/// <see cref="SyncJobsQueue.QueueName"/>-queue in de bestaande <c>AzureWebJobsStorage</c>-opslag —
/// geen nieuwe Azure-resource. <see cref="SyncJobProcessor"/> verwerkt het bericht en werkt de
/// status bij. Zo verdwijnt het stille-deelsucces-risico: een crash van de host tussen enqueue en
/// verwerking laat het bericht gewoon opnieuw zichtbaar worden op de queue in plaats van spoorloos
/// te verdwijnen zoals bij <c>Task.Run</c>.
/// </summary>
public static class AdminSyncFunction
{
    [Function("AdminSyncStatus")]
    public static Task<IActionResult> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/sync/status")] HttpRequest req,
        FunctionContext context) =>
        AdminEndpoint.ExecuteAsync(req, context.GetLogger("AdminSyncStatus"), "sync-status ophalen",
            async clubCode =>
            {
                using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand(
                    "SELECT TOP 1 [LastSyncTimestamp], [FetchSchedule] FROM [dbo].[AppSettings] WHERE [ClubCode] = @ClubCode",
                    connection);
                command.Parameters.AddWithValue("@ClubCode", clubCode);
                using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return new NotFoundObjectResult(new { error = "Geen AppSettings rij" });

                DateTime? lastSync = reader["LastSyncTimestamp"] != DBNull.Value
                    ? DateTime.SpecifyKind(Convert.ToDateTime(reader["LastSyncTimestamp"]), DateTimeKind.Utc)
                    : null;
                var fetchSchedule = reader["FetchSchedule"].ToString() ?? "0 0 4 * * *";

                Guid? jobId = Guid.TryParse(req.Query["jobId"], out var parsedJobId) ? parsedJobId : null;
                var job = await SyncJobsRepository.GetLatestOrByIdAsync(clubCode, jobId);

                return new OkObjectResult(new
                {
                    lastSyncTimestamp = lastSync,
                    fetchSchedule,
                    status = lastSync.HasValue ? "ok" : "geen-sync-uitgevoerd",
                    job = job is null ? null : new
                    {
                        id = job.Id,
                        status = job.Status,
                        weekOffsetFrom = job.WeekOffsetFrom,
                        weekOffsetTo = job.WeekOffsetTo,
                        createdAt = job.CreatedAt,
                        startedAt = job.StartedAt,
                        completedAt = job.CompletedAt,
                        errorMessage = job.ErrorMessage
                    }
                });
            });

    [Function("AdminSyncTrigger")]
    public static Task<IActionResult> Trigger(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/sync/trigger")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSyncTrigger");
        return AdminEndpoint.ExecuteAsync(req, log, "sync starten",
            async clubCode =>
            {
                // #1352/#1461/#1492: body + seizoensvenster via de gedeelde orkestratie (400 bij ongeldig).
                var venster = await SyncTriggerEndpointCore.BepaalVensterAsync(
                    await new StreamReader(req.Body).ReadToEndAsync(), DateTime.UtcNow.Year,
                    () => SystemUtilities.SeasonHelper.GetSeasonEndWeekOffsetAsync(log),
                    jaar => SystemUtilities.SeasonHelper.GetSeasonStartWeekOffsetOrNullAsync(jaar));
                if (venster.Fout is not null) return venster.Fout;
                int fromWeekOffset = venster.Van, toWeekOffset = venster.Tot;
                var jobId = Guid.NewGuid();

                await SyncJobsRepository.CreateAsync(jobId, clubCode, weekOffsetFrom: fromWeekOffset, weekOffsetTo: toWeekOffset);

                var queueClient = SyncJobsQueue.CreateClient();
                await queueClient.CreateIfNotExistsAsync();
                var message = new SyncJobMessage
                {
                    JobId = jobId,
                    ClubCode = clubCode,
                    WeekOffsetFrom = fromWeekOffset,
                    WeekOffsetTo = toWeekOffset
                };
                await queueClient.SendMessageAsync(JsonSerializer.Serialize(message));

                log.LogInformation("AdminSyncTrigger: job {JobId}, range {From} .. {To} (reset: {Reset}) — op de queue gezet", jobId, fromWeekOffset, toWeekOffset, venster.IsReset);

                return new ObjectResult(new
                {
                    status = "gestart",
                    jobId,
                    weekOffsetFrom = fromWeekOffset,
                    weekOffsetTo = toWeekOffset,
                    tijdstip = DateTime.UtcNow,
                    melding = "Sync gestart op achtergrond. Controleer de voortgang via /beheer/sync/status?jobId=" + jobId + "."
                }) { StatusCode = 202 };
            });
    }
}
