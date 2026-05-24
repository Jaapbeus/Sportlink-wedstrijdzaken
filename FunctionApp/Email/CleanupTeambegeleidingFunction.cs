using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

public static class CleanupTeambegeleidingFunction
{
    [Function("CleanupTeambegeleiding")]
    public static async Task Run(
        [TimerTrigger("0 0 4 1 * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupTeambegeleiding");
        log.LogInformation("AVG-cleanup gestart: avg.Teambegeleiding (rijen ouder dan 1 jaar verwijderen)");

        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            var connStr = SystemUtilities.DatabaseConfig.ConnectionString;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXEC [avg].[sp_CleanupTeambegeleiding]";
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();

            log.LogInformation("AVG-cleanup Teambegeleiding geslaagd");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup Teambegeleiding mislukt");
            throw;
        }
    }
}
