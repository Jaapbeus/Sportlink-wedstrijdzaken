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
/// </para>
/// <para>
/// <b>Trigger</b> roept nu <see cref="PostgresSyncPipeline.RunSyncAsync"/> aan (#890) — dezelfde
/// fire-and-forget-vorm als de SQL Server-tier. <c>toWeekOffset</c> komt sinds #890's
/// seizoensvertaling uit <see cref="PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync"/>.
/// </para>
/// </summary>
public static class AdminSyncFunction
{
    [Function("AdminSyncStatus")]
    public static async Task<IActionResult> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/sync/status")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSyncStatus");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            var clubCode = EasyAuthHelper.GetClubCodeFromRequest(req);
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

            return new OkObjectResult(new
            {
                lastSyncTimestamp = lastSync,
                fetchSchedule,
                status = lastSync.HasValue ? "ok" : "geen-sync-uitgevoerd"
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij opvragen sync-status");
            return new ObjectResult(new { error = "Ophalen sync-status mislukt" }) { StatusCode = 500 };
        }
    }

    [Function("AdminSyncTrigger")]
    public static async Task<IActionResult> Trigger(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/sync/trigger")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSyncTrigger");
        var correlationId = EasyAuthHelper.ExtractOrCreateCorrelationId(req);
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return authResult;
        using var traceScope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

        var toWeekOffset = await PostgresSeasonHelper.GetSeasonEndWeekOffsetAsync(log);
        log.LogInformation("AdminSyncTrigger: range -1 .. {To} — fire-and-forget gestart", toWeekOffset);

        // Fire-and-forget: zelfde vorm als de SQL Server-tier — client pollt /status op wijziging lastsynctimestamp.
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncFunction.RunConfiguredSyncAsync(-1, toWeekOffset, log);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Achtergrond sync mislukt");
            }
        });

        return new ObjectResult(new
        {
            status = "gestart",
            weekOffsetFrom = -1,
            weekOffsetTo = toWeekOffset,
            tijdstip = DateTime.UtcNow,
            melding = "Sync gestart op achtergrond. Controleer lastSyncTimestamp via /beheer/sync/status voor resultaat."
        })
        { StatusCode = 202 };
    }
}
