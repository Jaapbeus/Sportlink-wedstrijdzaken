using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Admin/AdminSyncFunction.cs</c> (#887).
/// <para>
/// <b>Status</b> is volledig vertaald: <c>SELECT TOP 1</c> → <c>LIMIT 1</c>, geen
/// <c>DateTime.SpecifyKind</c> nodig (Npgsql geeft <c>TIMESTAMPTZ</c> al terug met <c>Kind=Utc</c>).
/// </para>
/// <para>
/// <b>Trigger is bewust NIET vertaald.</b> De SQL Server-tier roept
/// <c>FetchAndStoreApiData.RunSyncAsync</c> aan — de volledige Sportlink-ETL-pipeline (staging →
/// merge → history). Die pipeline bestaat nog niet op de Postgres-tier; dat is exact de scope van
/// #890 ("Synchronisatie- en stagingpad vertalen"), nog niet gestart. Een sync "starten" die niets
/// doet zou stil falen — dus retourneert deze tier een expliciete 501 met verwijzing naar #890 in
/// plaats van een no-op 202 te faken.
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
    public static Task<IActionResult> Trigger(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "beheer/sync/trigger")] HttpRequest req,
        FunctionContext context)
    {
        var authResult = EasyAuthHelper.RequireAdmin(req);
        if (authResult != null) return Task.FromResult(authResult);

        return Task.FromResult<IActionResult>(new ObjectResult(new
        {
            error = "Sync-trigger is nog niet beschikbaar op de Postgres-tier — zie issue 890 " +
                    "(synchronisatie- en stagingpad). /api/beheer/sync/status werkt al wel."
        })
        { StatusCode = 501 });
    }
}
