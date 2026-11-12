using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Planner.Endpoints.Health;
using SportlinkFunction.Admin;

namespace SportlinkFunction.Planner;

/// <summary>Public aggregate readiness and admin-only health diagnostics for the SQL Server tier.</summary>
public static class HealthFunction
{
    [Function("Health")]
    public static async Task<IActionResult> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req,
        FunctionContext context)
    {
        var snapshot = await GetCachedHealthSnapshotAsync(req.HttpContext.RequestAborted);
        var response = snapshot.ToPublicResponse();
        return snapshot.Database == "unconfigured"
            ? new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable }
            : new OkObjectResult(response);
    }

    [Function("AdminHealth")]
    public static Task<IActionResult> AdminHealth(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "beheer/health")] HttpRequest req,
        FunctionContext context)
    {
        var log = context.GetLogger("AdminHealth");
        return AdminEndpoint.ExecuteZonderDatabaseAsync(req, log, "gezondheidsdetails ophalen", async () =>
        {
            var snapshot = await GetCachedHealthSnapshotAsync(req.HttpContext.RequestAborted);
            var response = snapshot.ToAdminResponse();
            return snapshot.Database == "unconfigured"
                ? new ObjectResult(response) { StatusCode = StatusCodes.Status503ServiceUnavailable }
                : new OkObjectResult(response);
        });
    }

    private static Task<HealthStatusSnapshot> GetCachedHealthSnapshotAsync(CancellationToken callerCancellation) =>
        HealthStatusCacheAccessor.Instance.GetAsync("SqlServer", CreateHealthSnapshotAsync, callerCancellation);

    private static async Task<HealthStatusSnapshot> CreateHealthSnapshotAsync()
    {
        var version = typeof(HealthFunction).Assembly.GetName().Version?.ToString(4) ?? "?";
        var (dbStatus, serverVersion) = await PlannerFunction.GetDatabaseStatusAsync();
        var settingsLoaded = !SystemUtilities.AppSettings.LastLoadFailed;
        return new HealthStatusSnapshot(
            dbStatus == "online" && settingsLoaded ? "ok" : "degraded",
            version,
            dbStatus,
            settingsLoaded,
            MigrationsCurrent: null,
            SchemaWarningPresent: false,
            TlsWarningPresent: false,
            Timestamp: DateTime.UtcNow,
            Tier: PlannerFunction.GetAssemblyMetadata("DatabaseTier") ?? "onbekend",
            Provider: PlannerFunction.GetAssemblyMetadata("DatabaseProvider") ?? "onbekend",
            ServerVersion: serverVersion,
            LastSync: null,
            SyncStale: null,
            TlsMode: null,
            TlsWarning: null,
            SchemaWarning: null,
            PendingMigrations: null);
    }
}
