using Database.Postgres;
using Microsoft.Azure.Functions.Worker;
using Npgsql;
using Planner.Shared.Avg;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// AVG-seizoensretentie voor de persoonskolommen in <c>his.matches</c>/<c>his.matchdetails</c> (#1698).
/// Regel en orkestratie: <see cref="HisPersoonsgegevensRetentie"/>; query's:
/// <see cref="PostgresHisRetentie"/>. SQL Server-tegenhanger:
/// <c>FunctionApp/Admin/CleanupHisPersoonsgegevensFunction.cs</c>. Dagelijks 05:00 UTC, buiten de
/// andere opschoontaken (03:00 zo, 04:00/04:30/04:45 de 1e, 05:15 dagelijks); waarom dagelijks staat
/// bij <see cref="HisPersoonsgegevensRetentie"/>.
/// </summary>
public static class CleanupHisPersoonsgegevensFunction
{
    [Function("CleanupHisPersoonsgegevens")]
    public static Task Run([TimerTrigger("0 0 5 * * *")] TimerInfo myTimer, FunctionContext context)
    {
        var log = context.GetLogger("CleanupHisPersoonsgegevens");
        return HisPersoonsgegevensRetentie.VoerTimerUitAsync(
            () => PostgresSystemUtilities.WaitForDatabaseAsync(log),
            OpenAsync,
            verbinding => PostgresHisRetentie.LeesSeizoenseindenAsync(verbinding),
            (verbinding, grens) => PostgresHisRetentie.WisTotEnMetAsync(verbinding, grens),
            log);
    }

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var verbinding = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
        await verbinding.OpenAsync();
        return verbinding;
    }
}
