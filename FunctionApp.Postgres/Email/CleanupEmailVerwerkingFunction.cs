using Database.Postgres;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Email/CleanupEmailVerwerkingFunction.cs</c> (#861).
/// Zelfde schema (wekelijks, zondag 03:00 UTC) en zelfde volgorde (ClassificatieCorrectie vóór
/// EmailVerwerking, #424) als de SQL Server-tier.
/// </summary>
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
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            await using var conn = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await conn.OpenAsync();

            await PostgresCleanupProcedures.CleanupClassificatieCorrectieAsync(conn);
            log.LogInformation("AVG-cleanup ClassificatieCorrectie geslaagd");

            await PostgresCleanupProcedures.CleanupEmailVerwerkingAsync(conn);
            log.LogInformation("AVG-cleanup EmailVerwerking geslaagd");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup EmailVerwerking/ClassificatieCorrectie mislukt");
            throw;
        }
    }
}
