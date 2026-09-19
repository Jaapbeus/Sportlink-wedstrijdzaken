using Microsoft.Extensions.Logging;
// Alias en niet de volledige naam: binnen namespace SportlinkFunction.Infrastructure zou
// "Planner.Shared..." tegen de eigen SportlinkFunction.Planner-namespace aanlopen.
using GedeeldeIssueReporter = Planner.Shared.Infrastructure.GitHubIssueReporter;

namespace SportlinkFunction.Infrastructure;

/// <summary>
/// SQL Server-tier-aansluiting op de gedeelde <see cref="GedeeldeIssueReporter"/>
/// (#1268). De rapportage-logica zelf — HTTP naar de GitHub API, deduplicatie op fingerprint en de
/// opbouw van de issue-body — is niet databasetier-specifiek en staat daarom in Planner.Shared.
/// <para>
/// Hier blijft alleen wat per tier verschilt: de <see cref="EgressGuard"/> van déze Function App
/// (#857) en het namespace-voorvoegsel waarmee de fingerprint de eerste eigen stackframe herkent.
/// Dat voorvoegsel is bewust tier-specifiek: één gedeelde lijst met beide voorvoegsels zou de
/// fingerprints van deze tier verschuiven en daarmee de dedup van reeds bestaande issues breken.
/// </para>
/// </summary>
internal static class FoutRapportage
{
    private const string EigenNamespacePrefix = "SportlinkFunction.";

    internal static Task RapporteerAsync(Exception ex, string functionName, ILogger log)
        => GedeeldeIssueReporter.ReportAsync(
            ex, functionName, log, EgressGuard.ExternalIntegrationsAllowed, EigenNamespacePrefix);
}
