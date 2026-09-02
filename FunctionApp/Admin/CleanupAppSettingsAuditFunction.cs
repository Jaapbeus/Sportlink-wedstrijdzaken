using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Admin;

/// <summary>
/// AVG-retentie voor dbo.AppSettingsAudit (#781, AVG art. 5 lid 1 sub e — opslagbeperking).
///
/// De tabel logt elke instellingenwijziging (AdminSettingsFunction.Put) zonder bewaartermijn.
/// [GewijzigdDoor] is een Entra-gebruikersnaam/UPN en [OudeWaarde]/[NieuweWaarde] kunnen
/// e-mailadressen bevatten (bijv. bij GraphMailbox of EmailReviewRecipient) — beide zijn
/// persoonsgegevens.
///
/// De bewaartermijn is configureerbaar via dbo.AppSettings.AppSettingsAuditBewaarDagen
/// (default 730 dagen / 24 maanden — een gedocumenteerd UITGANGSPUNT, geen definitief beleid;
/// zie de toelichting in Database/dbo/System Stored Procedures/sp_CleanupAppSettingsAudit.sql).
/// Groei is traag — rijen ontstaan alleen bij instellingenwijzigingen — dus een maandelijkse
/// cadans is voldoende, analoog aan CleanupTeambegeleidingFunction.
/// </summary>
public static class CleanupAppSettingsAuditFunction
{
    [Function("CleanupAppSettingsAudit")]
    public static async Task Run(
        [TimerTrigger("0 30 4 1 * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupAppSettingsAudit");
        log.LogInformation("AVG-cleanup gestart: dbo.AppSettingsAudit");

        try
        {
            await SystemUtilities.WaitForDatabaseAsync(log);

            var connStr = SystemUtilities.DatabaseConfig.ConnectionString;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXEC [dbo].[sp_CleanupAppSettingsAudit]";
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();

            log.LogInformation("AVG-cleanup AppSettingsAudit geslaagd");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup AppSettingsAudit mislukt");
            throw;
        }
    }
}
