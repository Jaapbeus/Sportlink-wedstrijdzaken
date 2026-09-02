using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres;

/// <summary>
/// Postgres-tier-equivalent van <c>SystemUtilities.WaitForDatabaseAsync</c> (#887). Simpelere
/// retry-logica dan de SQL Server-tier: die telt 20 pogingen à 15s specifiek af op Azure SQL
/// Serverless' auto-resume-venster (30-90s) — zonder bevestiging dat de Postgres-hosting een
/// vergelijkbaar auto-pause-gedrag heeft (zie ook §10 in ARCHITECTUUR-DATABASE-TIERS.md, #891) is
/// dat aantal hier niet zomaar overgenomen. 5 pogingen à 3s dekt een gewone, kortstondige
/// verbindingsstoring; herzie zodra de daadwerkelijke Postgres-hosting vaststaat.
/// </summary>
public static class PostgresSystemUtilities
{
    // #859: overschrijfbaar via omgevingsvariabelen (zelfde namen als de SQL Server-tier), zodat
    // deze waarden net als daar centraal instelbaar zijn i.p.v. een tweede hardcoded aanname.
    public static async Task WaitForDatabaseAsync(ILogger log)
    {
        var maxRetries = GetConfiguredInt("DbWaitMaxRetries", 5);
        var delayMs = GetConfiguredInt("DbWaitDelayMs", 3000);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
                await connection.OpenAsync();
                log.LogInformation("Database connection established.");
                await PostgresAppSettings.LoadSettingsAsync(log);
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Database connection failed. Retry {Attempt}/{MaxRetries}", attempt, maxRetries);
                if (attempt < maxRetries)
                    await Task.Delay(delayMs);
            }
        }

        throw new Exception("Unable to establish a database connection after multiple attempts.");
    }

    private static int GetConfiguredInt(string envVarName, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVarName);
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}
