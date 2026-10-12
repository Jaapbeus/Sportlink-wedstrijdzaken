using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Planner.Shared.Avg;

namespace SportlinkFunction.Admin;

/// <summary>
/// AVG-seizoensretentie voor de persoonskolommen in <c>[his].[matches]</c>/<c>[his].[matchdetails]</c>
/// (#1698) — SQL Server-tegenhanger van <c>FunctionApp.Postgres/Admin/CleanupHisPersoonsgegevensFunction.cs</c>.
/// Regel en orkestratie: <see cref="HisPersoonsgegevensRetentie"/>; query's:
/// <see cref="SqlServerHisRetentie"/>. Dagelijks 05:00 UTC.
/// </summary>
public static class CleanupHisPersoonsgegevensFunction
{
    [Function("CleanupHisPersoonsgegevens")]
    public static Task Run([TimerTrigger("0 0 5 * * *")] TimerInfo myTimer, FunctionContext context)
    {
        var log = context.GetLogger("CleanupHisPersoonsgegevens");
        return HisPersoonsgegevensRetentie.VoerTimerUitAsync(
            () => SystemUtilities.WaitForDatabaseAsync(log),
            async () =>
            {
                var sql = new SqlConnection(SystemUtilities.DatabaseConfig.ConnectionString);
                await sql.OpenAsync();
                return sql;
            },
            SqlServerHisRetentie.LeesSeizoenseindenAsync,
            SqlServerHisRetentie.WisTotEnMetAsync,
            log);
    }
}
