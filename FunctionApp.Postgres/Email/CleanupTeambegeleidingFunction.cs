using Database.Postgres;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/CleanupTeambegeleidingFunction.cs</c> (#861).
/// Zelfde schema (maandelijks, 1e van de maand 04:00 UTC) als de SQL Server-tier.
/// </summary>
public static class CleanupTeambegeleidingFunction
{
    [Function("CleanupTeambegeleiding")]
    public static async Task Run(
        [TimerTrigger("0 0 4 1 * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupTeambegeleiding");
        log.LogInformation("AVG-cleanup gestart: avg.teambegeleiding + avg.importlog");

        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            await using var conn = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await conn.OpenAsync();

            await PostgresCleanupProcedures.CleanupTeambegeleidingAsync(conn);
            log.LogInformation("AVG-cleanup Teambegeleiding geslaagd");

            await PostgresCleanupProcedures.CleanupImportLogAsync(conn);
            log.LogInformation("AVG-cleanup ImportLog geslaagd");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup Teambegeleiding/ImportLog mislukt");
            throw;
        }
    }
}
