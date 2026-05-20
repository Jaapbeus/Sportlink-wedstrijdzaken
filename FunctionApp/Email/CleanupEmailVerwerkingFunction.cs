using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Email;

public static class CleanupEmailVerwerkingFunction
{
    [Function("CleanupEmailVerwerking")]
    public static async Task Run(
        [TimerTrigger("0 0 3 * * 0")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupEmailVerwerking");
        log.LogInformation("AVG-cleanup gestart: planner.EmailVerwerking (30d anonimiseren, 90d verwijderen)");

        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            var connStr = SystemUtilities.DatabaseConfig.ConnectionString;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXEC [planner].[sp_CleanupEmailVerwerking]";
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();

            log.LogInformation("AVG-cleanup EmailVerwerking geslaagd");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup EmailVerwerking mislukt");
            throw;
        }
    }
}
