using Planner.Shared.Infrastructure;

namespace FunctionApp.Postgres.Infrastructure;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp/Infrastructure/EgressGuard.cs</c> (#857/#890): dunne
/// adapter op de gedeelde beslisregel <see cref="LokaleOntwikkelomgeving.ExterneIntegratiesToegestaan()"/>
/// (#1700). Open in productie (Azure zonder lokale schakelaar) of met <c>AllowExternalIntegrations=true</c>.
/// </summary>
public static class EgressGuard
{
    public static bool ExternalIntegrationsAllowed() => LokaleOntwikkelomgeving.ExterneIntegratiesToegestaan();
}
