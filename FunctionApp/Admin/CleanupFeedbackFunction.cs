using Microsoft.Azure.Functions.Worker;
using Planner.Endpoints.Feedback;
using SportlinkFunction.Feedback;

namespace SportlinkFunction.Admin;

/// <summary>
/// Dagelijkse bewaartermijn-run voor feedbackmeldingen (#764, #1476) — SQL Server-tier-tegenhanger van <c>FunctionApp.Postgres/Admin/CleanupFeedbackFunction.cs</c>. De eigenlijke
/// run staat in <see cref="FeedbackEndpointCore.VoerRetentieTimerAsync"/>: de GitHub-statuscontrole
/// (uitsluitend via de tier-eigen EgressGuard, #857), de anonimisering van de melder 24 maanden na
/// sluiting van het issue, het wissen van technische context na 90 dagen en van inzageregels na
/// 24 maanden. Dagelijks (05:15 UTC, ruim buiten de maandelijkse AVG-opschoontaken van 04:00/04:30),
/// omdat de statuscontrole per run begrensd is op 200 issues. Zie docs/FEEDBACK.md.
/// </summary>
public static class CleanupFeedbackFunction
{
    [Function("CleanupFeedback")]
    public static Task Run(
        [TimerTrigger("0 15 5 * * *")] TimerInfo myTimer,
        FunctionContext context)
    {
        var log = context.GetLogger("CleanupFeedback");
        return FeedbackEndpointCore.VoerRetentieTimerAsync(
            () => SystemUtilities.WaitForDatabaseAsync(log), new SqlFeedbackStore(SystemUtilities.DatabaseConfig.ConnectionString), Infrastructure.EgressGuard.ExternalIntegrationsAllowed(), log);
    }
}
