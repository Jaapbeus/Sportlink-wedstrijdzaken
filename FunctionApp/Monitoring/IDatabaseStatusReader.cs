using Planner.Shared.Monitoring;

namespace SportlinkFunction.Monitoring;

/// <summary>
/// Leest de management-plane status van de Azure SQL Database — géén databaseverbinding. Bewust
/// abstract van de concrete Azure Management API-aanroep zodat <c>DatabaseUitvalMonitorFunction</c>
/// unit-testbaar is zonder een echte Azure-omgeving (#831).
///
/// <para>
/// De uitkomst (<see cref="DatabaseStatusInfo"/>) en alles wat er daarna mee gebeurt staan sinds
/// #1268 in <see cref="DatabaseUitvalCore"/>, gedeeld met de Postgres-tier. Wat hier tier-specifiek
/// blijft is uitsluitend hóe de status wordt opgehaald.
/// </para>
/// </summary>
public interface IDatabaseStatusReader
{
    Task<DatabaseStatusInfo> LeesStatusAsync(
        string subscriptionId, string resourceGroup, string sqlServerName, string sqlDatabaseName);
}
