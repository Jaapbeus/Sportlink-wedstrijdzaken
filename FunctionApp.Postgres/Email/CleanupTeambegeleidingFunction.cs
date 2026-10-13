using Database.Postgres;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;
using Planner.Shared.Teambegeleiding;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/CleanupTeambegeleidingFunction.cs</c> (#861).
/// Zelfde schema (maandelijks, 1e van de maand 04:00 UTC) als de SQL Server-tier. Sinds #1701 ruimt
/// dezelfde run ook verlopen regels van het inzagelog (<c>avg.teambegeleidinginzagelog</c>) op.
/// </summary>
public static class CleanupTeambegeleidingFunction
{
    [Function("CleanupTeambegeleiding")]
    public static async Task Run(
        [TimerTrigger("0 0 4 1 * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupTeambegeleiding");
        log.LogInformation("AVG-cleanup gestart: avg.teambegeleiding + avg.importlog + avg.teambegeleidinginzagelog");

        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            await using var conn = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await conn.OpenAsync();

            await PostgresCleanupProcedures.CleanupTeambegeleidingAsync(conn);
            log.LogInformation("AVG-cleanup Teambegeleiding geslaagd");

            await PostgresCleanupProcedures.CleanupImportLogAsync(conn);
            log.LogInformation("AVG-cleanup ImportLog geslaagd");

            // #1701: inzagelog — alleen een telling in het log, nooit inhoud.
            var inzage = await PostgresCleanupProcedures.CleanupTeambegeleidingInzageLogAsync(
                conn, DateTime.UtcNow, TeambegeleidingInzageRetentie.InzageLogMaanden);
            log.LogInformation("AVG-cleanup TeambegeleidingInzageLog geslaagd: {Aantal} regels verwijderd", inzage);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup Teambegeleiding/ImportLog/InzageLog mislukt");
            throw;
        }
    }
}
