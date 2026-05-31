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
        log.LogInformation("AVG-cleanup gestart: ClassificatieCorrectie + EmailVerwerking");

        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            var connStr = SystemUtilities.DatabaseConfig.ConnectionString;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // Volgorde is kritiek: ClassificatieCorrectie heeft FK naar EmailVerwerking.
            // ClassificatieCorrectie eerst opruimen zodat de DELETE op EmailVerwerking niet
            // faalt door een referentie-constraint. (#424)
            await using (var cmd1 = conn.CreateCommand())
            {
                cmd1.CommandText = "EXEC [planner].[sp_CleanupClassificatieCorrectie]";
                cmd1.CommandTimeout = 120;
                await cmd1.ExecuteNonQueryAsync();
                log.LogInformation("AVG-cleanup ClassificatieCorrectie geslaagd");
            }

            await using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = "EXEC [planner].[sp_CleanupEmailVerwerking]";
                cmd2.CommandTimeout = 120;
                await cmd2.ExecuteNonQueryAsync();
                log.LogInformation("AVG-cleanup EmailVerwerking geslaagd");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup EmailVerwerking/ClassificatieCorrectie mislukt");
            throw;
        }
    }
}
