using Database.Postgres;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FunctionApp.Postgres.Admin;

/// <summary>
/// AVG-retentie voor <c>public.sportlinkmutationaudit</c> (#1114, epic #986) — Postgres-tegenhanger
/// van <c>FunctionApp/Admin/CleanupSportlinkMutationAuditFunction.cs</c>. Maandelijks, 1e van de
/// maand 04:45 UTC: een kwartier ná <see cref="CleanupAppSettingsAuditFunction"/> (04:30), dat zelf
/// een half uur ná de teambegeleiding-opschoning (04:00) draait — de opschoontaken overlappen zo
/// niet op dezelfde databaseverbinding.
///
/// <para>
/// <b>Waarom deze taak bestaat.</b> De audittabel legt bij elke Sportlink-mutatiepoging een rij
/// vast met het e-mailadres/UPN van de beheerder die de actie triggerde (<c>triggerddoor</c>). Dat
/// is een persoonsgegeven, en er was — anders dan voor <c>appsettingsaudit</c> — geen enkel
/// mechanisme dat er ooit iets van verwijderde (AVG art. 5 lid 1 sub e, opslagbeperking).
/// </para>
/// <para>
/// De bewaartermijn is configureerbaar via <c>public.appsettings.sportlinkmutationauditbewaardagen</c>
/// (migratie 017; default 365 dagen — een gedocumenteerd UITGANGSPUNT dat de eigenaar als DPO
/// vaststelt, zie de toelichting bij <see cref="PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync"/>).
/// Het log groeit per mutatie, maar mutaties zijn handmatige beheerdersacties — een maandelijkse
/// cadans volstaat ruim.
/// </para>
/// </summary>
public static class CleanupSportlinkMutationAuditFunction
{
    [Function("CleanupSportlinkMutationAudit")]
    public static async Task Run(
        [TimerTrigger("0 45 4 1 * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupSportlinkMutationAudit");
        log.LogInformation("AVG-cleanup gestart: public.sportlinkmutationaudit");

        try
        {
            await PostgresSystemUtilities.WaitForDatabaseAsync(log);

            await using var conn = new NpgsqlConnection(PostgresDatabaseConfig.ConnectionString);
            await conn.OpenAsync();

            await PostgresCleanupProcedures.CleanupSportlinkMutationAuditAsync(conn);
            log.LogInformation("AVG-cleanup SportlinkMutationAudit geslaagd");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AVG-cleanup SportlinkMutationAudit mislukt");
            throw;
        }
    }
}
