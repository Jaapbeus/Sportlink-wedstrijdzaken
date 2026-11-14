using Microsoft.Azure.Functions.Worker;
using Planner.Shared.Sync;

namespace FunctionApp.Postgres.Sync;

/// <summary>
/// Herstel van onderbroken sync-jobs (#1839) — Postgres-tegenhanger van
/// <c>FunctionApp/Sync/SyncJobRecoveryFunction.cs</c>. Elke 15 minuten: een job die 30 minuten op
/// <c>running</c> staat geldt als onderbroken en wordt hoogstens 2 keer automatisch opnieuw
/// aangeboden, daarna <c>failed</c> met een melding. Beleid, drempels en orkestratie staan in
/// <see cref="SyncJobRecoveryPolicy"/> en <see cref="SyncJobRecoveryCore"/>; hier alleen de tier-SQL
/// en de enqueue op de bestaande queue (geen nieuwe Azure-resource).
/// </summary>
public static class SyncJobRecoveryFunction
{
    [Function("SyncJobRecovery")]
    public static async Task Run(
        [TimerTrigger("0 */15 * * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("SyncJobRecovery");
        await PostgresSystemUtilities.WaitForDatabaseAsync(log);

        await SyncJobRecoveryCore.RunAsync(
            cutoff => SyncJobsRepository.RecoverInterruptedAsync(PostgresDatabaseConfig.ConnectionString, cutoff), SyncJobsQueue.CreateClient(), log);
    }
}
