using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Npgsql;

namespace FunctionApp.Postgres;

/// <summary>
/// /api/health voor de Postgres-tier (#891) — zelfde vorm en velden als de SQL Server-tier
/// (<c>FunctionApp/Planner/PlannerFunction.cs</c>, #863): <c>tier</c>/<c>provider</c> komen uit
/// build-time assembly-metadata (nooit een runtime-gok, dus ook gevuld als de database
/// onbereikbaar is), <c>serverVersion</c> komt aantoonbaar uit de database zelf.
/// <para>
/// <b>Geen "paused"-status:</b> de SQL Server-tier herkent Azure SQL's serverless auto-pause aan
/// foutnummer 40613 — een Azure-SQL-specifiek concept. Zonder bevestigde, vergelijkbare
/// auto-pause-laag voor de gekozen Postgres-hosting zou een "paused"-status hier verzonnen zijn;
/// een onbereikbare database is dus altijd "unavailable" of "timeout".
/// </para>
/// </summary>
public static class HealthFunction
{
    [Function("Health")]
    public static async Task<IActionResult> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req,
        FunctionContext context)
    {
        var version = typeof(HealthFunction).Assembly.GetName().Version?.ToString(4) ?? "?";
        var (dbStatus, serverVersion) = await GetDatabaseStatusAsync();

        return new OkObjectResult(new
        {
            status = dbStatus == "online" ? "ok" : "degraded",
            version,
            timestamp = DateTime.UtcNow,
            database = dbStatus,
            tier = GetAssemblyMetadata("DatabaseTier") ?? "onbekend",
            provider = GetAssemblyMetadata("DatabaseProvider") ?? "onbekend",
            serverVersion
        });
    }

    internal static string? GetAssemblyMetadata(string key) =>
        typeof(HealthFunction).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

    private static async Task<(string status, string? serverVersion)> GetDatabaseStatusAsync()
    {
        string connStr;
        try { connStr = PostgresDatabaseConfig.ConnectionString; }
        catch { return ("unconfigured", null); }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(cts.Token);
            await using var cmd = new NpgsqlCommand("SHOW server_version", conn) { CommandTimeout = 5 };
            var serverVersion = (string?)await cmd.ExecuteScalarAsync(cts.Token);
            return ("online", serverVersion);
        }
        catch (OperationCanceledException)
        {
            return ("timeout", null);
        }
        catch
        {
            return ("unavailable", null);
        }
    }
}
