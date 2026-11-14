using Planner.Shared.Sync;
using Planner.Endpoints.Http;

using SportlinkFunction.Infrastructure;
using Planner.Endpoints.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SportlinkFunction.Sync;

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
                await SyncJobsRepository.HerstelOnderbrokenAsync(context.GetLogger("AdminSyncStatus"));
                var jobs = await SyncJobsRepository.GetRecentAsync(SystemUtilities.DatabaseConfig.ConnectionString, clubCode, jobId);
                return await SyncLogEndpointCore.ReadAsync(lastSync, fetchSchedule, jobs, jobId,
                    () => SyncLogRepository.CleanupAsync(SystemUtilities.DatabaseConfig.ConnectionString, clubCode, DateTime.UtcNow),
                    () => SyncLogRepository.GetForJobsAsync(SystemUtilities.DatabaseConfig.ConnectionString, clubCode, jobs.Select(j => j.Id).ToArray()));
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
                // #1676: geen job aanmaken of op de queue zetten als de verwerking toch geblokkeerd wordt.
                if (UitgaandVerkeerCore.Blokkeer(EgressGuard.ExternalIntegrationsAllowed) is { } geblokkeerd) return geblokkeerd;

                // #1352/#1461/#1492: body + seizoensvenster via de gedeelde orkestratie (400 bij ongeldig).
                return await BoundedRequestBodyReader.ExecuteUtf8Async(req, async bodyText =>
                {
                    return await SyncLogEndpointCore.StartAsync(
                        bodyText, DateTime.UtcNow.Year,
                        async () =>
                        {
                            // De queue mag tijdens een lopende sync blijven accepteren. De doorrol
                            // gebruikt alleen de bestaande AppSettings-rijlock, geen staginglease.
                            await SqlServerSeasonRollover.EnsureAsync(
                                clubCode, SqlServerSeasonRollover.VandaagInNederland, log);
                            return await SystemUtilities.SeasonHelper.GetSeasonEndWeekOffsetAsync(log, clubCode);
                        },
                        jaar => SystemUtilities.SeasonHelper.GetSeasonStartWeekOffsetOrNullAsync(jaar, clubCode),
                        () => SyncJobsRepository.HerstelOnderbrokenAsync(log),
                        (from, to) => SyncJobsRepository.GetOrCreateActiveAsync(SystemUtilities.DatabaseConfig.ConnectionString, Guid.NewGuid(), clubCode, from, to),
                        job => SyncLogEndpointCore.EnqueueAsync(SyncJobsQueue.CreateClient(), job, clubCode),
                        id => SyncJobsRepository.MarkEnqueueFailedAsync(SystemUtilities.DatabaseConfig.ConnectionString, id, clubCode),
                        id => SyncLogRepository.GetForJobsAsync(SystemUtilities.DatabaseConfig.ConnectionString, clubCode, new[] { id }));
                });
            });
    }

}
