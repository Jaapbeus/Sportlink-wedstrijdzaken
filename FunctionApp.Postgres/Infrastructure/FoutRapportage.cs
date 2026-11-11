using Microsoft.Extensions.Logging;
// Alias en niet de volledige naam: binnen namespace FunctionApp.Postgres.Infrastructure zou
// "Planner.Shared..." tegen de eigen FunctionApp.Postgres.Planner-namespace aanlopen.
using GedeeldeIssueReporter = Planner.Shared.Infrastructure.GitHubIssueReporter;

namespace FunctionApp.Postgres.Infrastructure;

/// <summary>
/// Reports errors from this Function App through the shared <see cref="GedeeldeIssueReporter"/>.
/// <para>
/// This adapter supplies this app's <see cref="EgressGuard"/> and namespace prefix for recognizing
/// the first application stack frame. Keep the prefix tier-specific: changing it shifts fingerprints
/// and can break deduplication of existing issues.
/// </para>
/// </summary>
internal static class FoutRapportage
{
    private const string EigenNamespacePrefix = "FunctionApp.Postgres.";

    internal static Task RapporteerAsync(Exception ex, string functionName, ILogger log)
        => GedeeldeIssueReporter.ReportAsync(
            ex, functionName, log, EgressGuard.ExternalIntegrationsAllowed, EigenNamespacePrefix);
}
