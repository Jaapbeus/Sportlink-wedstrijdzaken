using Microsoft.Azure.Functions.Worker;
using Planner.Endpoints.Avg;
using SportlinkFunction.Feedback;

namespace SportlinkFunction.Admin;

/// <summary>
/// Dagelijkse AVG-retentierun (05:15 UTC) — SQL Server-tier-tegenhanger van <c>FunctionApp.Postgres/Admin/CleanupFeedbackFunction.cs</c>. De
/// eigenlijke run staat in <see cref="DagelijkseAvgRetentie.VoerUitAsync"/>, met twee onafhankelijke
/// stappen: (1) de bewaartermijnen van feedbackmeldingen (#764, #1476): de GitHub-statuscontrole
/// (uitsluitend via de tier-eigen EgressGuard, #857), de anonimisering van de melder 24 maanden na
/// sluiting van het issue, het wissen van technische context na 90 dagen en van inzageregels na
/// 24 maanden; (2) de seizoensretentie van de persoonskolommen in de historietabellen (#1698).
/// Dagelijks omdat de statuscontrole per run begrensd is op 200 issues en omdat een (reset-)sync
/// de historiekolommen van een afgelopen seizoen opnieuw kan vullen. De functienaam blijft
/// <c>CleanupFeedback</c> zodat bestaande bewaking en logs aansluiten. Zie docs/FEEDBACK.md en
/// SECURITY.md (Laag 6).
/// </summary>
public static class CleanupFeedbackFunction
{
    [Function("CleanupFeedback")]
    public static Task Run(
        [TimerTrigger("0 15 5 * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupFeedback");
        return DagelijkseAvgRetentie.VoerUitAsync(
            () => SystemUtilities.WaitForDatabaseAsync(log), new SqlFeedbackStore(SystemUtilities.DatabaseConfig.ConnectionString), Infrastructure.EgressGuard.ExternalIntegrationsAllowed(), new SqlServerHisRetentie(SystemUtilities.DatabaseConfig.ConnectionString), log);
    }
}
