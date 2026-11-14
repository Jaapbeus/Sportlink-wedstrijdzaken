using Planner.Shared.Sync;
using Planner.Endpoints.Http;

using FunctionApp.Postgres.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;
using FunctionApp.Postgres.Sync;
using Planner.Endpoints.Admin;

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
                await SyncJobsRepository.HerstelOnderbrokenAsync(context.GetLogger("AdminSyncStatus"));
                var jobs = await SyncJobsRepository.GetRecentAsync(PostgresDatabaseConfig.ConnectionString, clubCode, jobId);
                return await SyncLogEndpointCore.ReadAsync(lastSync, fetchSchedule, jobs, jobId,
                    () => SyncLogRepository.CleanupAsync(PostgresDatabaseConfig.ConnectionString, clubCode, DateTime.UtcNow),
                    () => SyncLogRepository.GetForJobsAsync(PostgresDatabaseConfig.ConnectionString, clubCode, jobs.Select(j => j.Id).ToArray()));
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
                if (UitgaandVerkeerCore.Blokkeer(EgressGuard.ExternalIntegrationsAllowed) is { } egressAntwoord) return egressAntwoord;

                // #1352/#1461/#1492: body + seizoensvenster via de gedeelde orkestratie (400 bij ongeldig).
                return await BoundedRequestBodyReader.ExecuteUtf8Async(req, async bodyText =>
                {
                    return await SyncLogEndpointCore.StartAsync(
                        bodyText, DateTime.UtcNow.Year,
                        async () => { await PostgresSeasonHelper.EnsureSeasonsAsync(log); return await PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync(log, clubCode); },
                        jaar => PostgresSeasonHelper.GetSeasonStartWeekOffsetOrNullAsync(jaar, clubCode),
                        () => SyncJobsRepository.HerstelOnderbrokenAsync(log),
                        (from, to) => SyncJobsRepository.GetOrCreateActiveAsync(PostgresDatabaseConfig.ConnectionString, Guid.NewGuid(), clubCode, from, to),
                        job => SyncLogEndpointCore.EnqueueAsync(SyncJobsQueue.CreateClient(), job, clubCode),
                        id => SyncJobsRepository.MarkEnqueueFailedAsync(PostgresDatabaseConfig.ConnectionString, id, clubCode),
                        id => SyncLogRepository.GetForJobsAsync(PostgresDatabaseConfig.ConnectionString, clubCode, new[] { id }));
                });
            });
    }

}
