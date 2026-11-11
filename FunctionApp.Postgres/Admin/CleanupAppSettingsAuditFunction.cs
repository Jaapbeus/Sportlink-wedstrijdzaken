using Database.Postgres;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// Past de bewaartermijn toe op <c>public.appsettingsaudit</c> via een maandelijkse timer op de
/// eerste dag van de maand om 04:30 UTC. De taak draait bewust een half uur ná
/// <see cref="Email.CleanupTeambegeleidingFunction"/> (04:00), zodat de twee opschoontaken elkaar
/// niet op dezelfde databaseverbinding overlappen.
///
/// <para>
/// <b>Waarom deze taak bestaat.</b> <c>public.appsettingsaudit</c> legt elke instellingswijziging
/// vast en bevat op meerdere plekken persoonsgegevens: <c>gewijzigddoor</c> is een
/// Entra-gebruikersnaam/UPN, en <c>oudewaarde</c>/<c>nieuwewaarde</c> kunnen e-mailadressen
/// bevatten (bijvoorbeeld bij een wijziging van <c>GraphMailbox</c> of
/// <c>EmailReviewRecipient</c>). De taak past daarom de ingestelde bewaartermijn toe op deze
/// persoonsgegevens, in lijn met AVG art. 5 lid 1 sub e (opslagbeperking).
/// </para>
/// <para>
/// De bewaartermijn is configureerbaar via <c>public.appsettings.appsettingsauditbewaardagen</c>
/// (default 730 dagen / 24 maanden — een gedocumenteerd UITGANGSPUNT, geen definitief beleid; zie
/// de toelichting bij <see cref="PostgresCleanupProcedures.CleanupAppSettingsAuditAsync"/> en bij
/// de SQL Server-procedure). Groei is traag — rijen ontstaan alleen bij instellingenwijzigingen —
/// dus een maandelijkse cadans volstaat.
/// </para>
/// </summary>
public static class CleanupAppSettingsAuditFunction
{
    [Function("CleanupAppSettingsAudit")]
    public static async Task Run(
        [TimerTrigger("0 30 4 1 * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupAppSettingsAudit");
        log.LogInformation("AVG-cleanup gestart: public.appsettingsaudit");

        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            await using var conn = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await conn.OpenAsync();

            await PostgresCleanupProcedures.CleanupAppSettingsAuditAsync(conn);
            log.LogInformation("AVG-cleanup AppSettingsAudit geslaagd");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup AppSettingsAudit mislukt");
            throw;
        }
    }
}
