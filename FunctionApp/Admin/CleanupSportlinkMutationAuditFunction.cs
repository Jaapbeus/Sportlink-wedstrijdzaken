using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// AVG-retentie voor dbo.SportlinkMutationAudit (#1114, epic #986; AVG art. 5 lid 1 sub e —
/// opslagbeperking). De tabel logt elke Sportlink-mutatiepoging met [TriggerdDoor] — het
/// e-mailadres/UPN van de beheerder die de actie triggerde, een persoonsgegeven — en had, anders
/// dan dbo.AppSettingsAudit, geen enkele opschoning.
///
/// Bewaartermijn configureerbaar via dbo.AppSettings.SportlinkMutationAuditBewaarDagen (default
/// 365 dagen — een gedocumenteerd UITGANGSPUNT, geen definitief beleid; zie
/// Database/dbo/System Stored Procedures/sp_CleanupSportlinkMutationAudit.sql). Maandelijks op de
/// 1e om 04:45 UTC, een kwartier ná CleanupAppSettingsAuditFunction (04:30), zodat de
/// opschoontaken niet op dezelfde verbinding overlappen.
/// </summary>
public static class CleanupSportlinkMutationAuditFunction
{
    [Function("CleanupSportlinkMutationAudit")]
    public static async Task Run(
        [TimerTrigger("0 45 4 1 * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupSportlinkMutationAudit");
        log.LogInformation("AVG-cleanup gestart: dbo.SportlinkMutationAudit");

        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            var connStr = SystemUtilities.DatabaseConfig.ConnectionString;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXEC [dbo].[sp_CleanupSportlinkMutationAudit]";
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();

            log.LogInformation("AVG-cleanup SportlinkMutationAudit geslaagd");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup SportlinkMutationAudit mislukt");
            throw;
        }
    }
}
