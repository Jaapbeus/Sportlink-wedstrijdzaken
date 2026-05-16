using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// Admin API voor synchronisatie. v2 — #89.
///
/// GET  /api/admin/sync/status   → laatste sync timestamp + huidige FetchSchedule
/// POST /api/admin/sync/trigger  → start synchronisatie (intern aanroep van FetchAndStoreApiData.RunSyncAsync)
/// </summary>
public static class AdminSyncFunction
{
    [Function("AdminSyncStatus")]
    public static async Task<IActionResult> Status(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "beheer/sync/status")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSyncStatus");
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            using var connection = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(
                "SELECT TOP 1 [LastSyncTimestamp], [FetchSchedule] FROM [dbo].[AppSettings]",
                connection);
            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return new NotFoundObjectResult(new { error = "Geen AppSettings rij" });

            DateTime? lastSync = reader["LastSyncTimestamp"] != DBNull.Value
                ? Convert.ToDateTime(reader["LastSyncTimestamp"])
                : null;
            var fetchSchedule = reader["FetchSchedule"].ToString() ?? "0 0 4 * * *";

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
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    [Function("AdminSyncTrigger")]
    public static async Task<IActionResult> Trigger(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "beheer/sync/trigger")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminSyncTrigger");
        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);
            await SystemUtilities.AppSettings.LoadSettingsAsync(log);

            var sportlinkApiUrl = SystemUtilities.AppSettings.GetSetting("sportlinkApiUrl");
            if (string.IsNullOrEmpty(sportlinkApiUrl))
                return new ObjectResult(new { error = "sportlinkApiUrl niet geconfigureerd" }) { StatusCode = 500 };

            var sportlinkClientId = $"clientId={SystemUtilities.AppSettings.GetSetting("sportlinkClientId")}";

            int toWeekOffset = await SystemUtilities.SeasonHelper.GetSeasonEndWeekOffsetAsync(log);
            log.LogInformation("AdminSyncTrigger: range -1 .. {To}", toWeekOffset);

            // Hergebruik bestaande sync-logica direct (geen HTTP, geen verschil met timer trigger)
            await FetchAndStoreApiData.RunSyncAsync(-1, toWeekOffset, sportlinkApiUrl, sportlinkClientId, log);

            return new OkObjectResult(new
            {
                status = "ok",
                weekOffsetFrom = -1,
                weekOffsetTo = toWeekOffset,
                tijdstip = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fout bij triggeren van sync");
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }
}
