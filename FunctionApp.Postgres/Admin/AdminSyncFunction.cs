using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;
using FunctionApp.Postgres.Sync;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminSyncFunction.cs</c> (#887/#890).
/// <para>
/// <b>Status</b> is volledig vertaald: <c>SELECT TOP 1</c> → <c>LIMIT 1</c>, geen
/// <c>DateTime.SpecifyKind</c> nodig (Npgsql geeft <c>TIMESTAMPTZ</c> al terug met <c>Kind=Utc</c>).
/// Retourneert nu ook de meest recente <c>syncjobs</c>-rij (optioneel gefilterd op <c>jobId</c>
/// query-param), zodat de GUI een specifieke job kan pollen (#1138).
/// </para>
/// <para>
/// <b>Trigger</b> is niet langer fire-and-forget via <c>Task.Run</c> (#1138, #415): de HTTP-call
/// schrijft een <c>syncjobs</c>-rij (status <c>pending</c>) en zet een bericht op de
/// <see cref="SyncJobsQueue.QueueName"/>-queue in de bestaande <c>AzureWebJobsStorage</c>-opslag —
/// geen nieuwe Azure-resource. <see cref="SyncJobProcessor"/> verwerkt het bericht en werkt de
/// status bij. Zo verdwijnt het stille-deelsucces-risico: een crash van de host tussen enqueue en
/// verwerking laat het bericht gewoon opnieuw zichtbaar worden op de queue in plaats van spoorloos
/// te verdwijnen zoals bij <c>Task.Run</c>.
/// </para>
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
                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();

                await using var command = new NpgsqlCommand(
                    "SELECT lastsynctimestamp, fetchschedule FROM public.appsettings WHERE clubcode = @clubcode LIMIT 1",
                    connection);
                command.Parameters.AddWithValue("clubcode", clubCode);
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return new NotFoundObjectResult(new { error = "Geen AppSettings rij" });

                DateTime? lastSync = reader["lastsynctimestamp"] != DBNull.Value
                    ? Convert.ToDateTime(reader["lastsynctimestamp"])
                    : null;
                var fetchSchedule = reader["fetchschedule"].ToString() ?? "0 0 4 * * *";

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
                // #861: rol public.season zo nodig door vóór het venster gelezen wordt.
                await PostgresSeasonHelper.EnsureSeasonsAsync(log);
                var toWeekOffset = await PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync(log);
                var jobId = Guid.NewGuid();

                await SyncJobsRepository.CreateAsync(jobId, clubCode, weekOffsetFrom: -1, weekOffsetTo: toWeekOffset);

                var storageVerbinding = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                    ?? throw new InvalidOperationException(
                        "AzureWebJobsStorage ontbreekt — vereist voor de Azure Functions-host zelf.");
                var queueClient = SyncJobsQueue.CreateClient(storageVerbinding);
                await queueClient.CreateIfNotExistsAsync();
                var message = new SyncJobMessage
                {
                    JobId = jobId,
                    ClubCode = clubCode,
                    WeekOffsetFrom = -1,
                    WeekOffsetTo = toWeekOffset
                };
                await queueClient.SendMessageAsync(JsonSerializer.Serialize(message));

                log.LogInformation("AdminSyncTrigger: job {JobId}, range -1 .. {To} — op de queue gezet", jobId, toWeekOffset);

                return new ObjectResult(new
                {
                    status = "gestart",
                    jobId,
                    weekOffsetFrom = -1,
                    weekOffsetTo = toWeekOffset,
                    tijdstip = DateTime.UtcNow,
                    melding = "Sync gestart op achtergrond. Controleer de voortgang via /beheer/sync/status?jobId=" + jobId + "."
                })
                { StatusCode = 202 };
            });
    }
}
