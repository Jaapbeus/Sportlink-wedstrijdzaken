using Microsoft.Extensions.Logging;
// Alias en niet de volledige naam: binnen namespace FunctionApp.Postgres.Infrastructure zou
// "Planner.Shared..." tegen de eigen FunctionApp.Postgres.Planner-namespace aanlopen.
using GedeeldeIssueReporter = Planner.Shared.Infrastructure.GitHubIssueReporter;

namespace FunctionApp.Postgres.Infrastructure;

/// <summary>
/// Postgres-tier-aansluiting op de gedeelde <see cref="GedeeldeIssueReporter"/>
/// (#1268). Tot dit bestand bestond had alleen de SQL Server-tier automatische foutrapportage,
/// terwijl de Postgres-tier degene is die in productie draait: runtimefouten bleven daar een
/// logregel die niemand las. Beide tiers zijn gelijkwaardig (CLAUDE.md, "Multi-tier
/// databasestrategie"), dus de rapportage hoort op beide.
/// <para>
/// Hier staat alleen wat per tier verschilt: de <see cref="EgressGuard"/> van déze Function App
/// (#857) en het namespace-voorvoegsel waarmee de fingerprint de eerste eigen stackframe herkent.
/// </para>
/// </summary>
internal static class FoutRapportage
{
    private const string EigenNamespacePrefix = "FunctionApp.Postgres.";

    internal static Task RapporteerAsync(Exception ex, string functionName, ILogger log)
        => GedeeldeIssueReporter.ReportAsync(
            ex, functionName, log, EgressGuard.ExternalIntegrationsAllowed, EigenNamespacePrefix);
}
