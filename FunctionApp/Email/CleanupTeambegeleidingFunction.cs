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
        log.LogInformation("AVG-cleanup gestart: avg.Teambegeleiding + avg.ImportLog");

        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            var connStr = SystemUtilities.DatabaseConfig.ConnectionString;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            await using (var cmd1 = conn.CreateCommand())
            {
                cmd1.CommandText = "EXEC [avg].[sp_CleanupTeambegeleiding]";
                cmd1.CommandTimeout = 120;
                await cmd1.ExecuteNonQueryAsync();
                log.LogInformation("AVG-cleanup Teambegeleiding geslaagd");
            }

            // ImportLog: anonimiseer ImporterendeDoor + CsvBestand na 90d, verwijder na 1 jaar. (#426)
            await using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = "EXEC [avg].[sp_CleanupImportLog]";
                cmd2.CommandTimeout = 120;
                await cmd2.ExecuteNonQueryAsync();
                log.LogInformation("AVG-cleanup ImportLog geslaagd");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup Teambegeleiding/ImportLog mislukt");
            throw;
        }
    }
}
