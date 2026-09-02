namespace SportlinkFunction.Monitoring;

/// <summary>
/// Resultaat van een management-plane statuscontrole van de Azure SQL Database.
/// </summary>
/// <param name="Status">
/// Ruwe <c>properties.status</c>-waarde van de Azure SQL Database REST API, bijv. "Online", "Paused",
/// "Pausing" of "Resuming".
/// </param>
/// <param name="PausedSinceUtc">
/// De <c>properties.pausedDate</c>-waarde (UTC), of <c>null</c> als de database niet gepauzeerd is of
/// als de API dit veld niet teruggeeft.
/// </param>
public sealed record DatabaseStatusInfo(string Status, DateTime? PausedSinceUtc);

/// <summary>
/// Leest de management-plane status van de Azure SQL Database — géén databaseverbinding. Bewust
/// abstract van de concrete Azure Management API-aanroep zodat <c>DatabaseUitvalMonitorFunction</c>
/// unit-testbaar is zonder een echte Azure-omgeving (#831).
/// </summary>
public interface IDatabaseStatusReader
{
    Task<DatabaseStatusInfo> LeesStatusAsync(
        string subscriptionId, string resourceGroup, string sqlServerName, string sqlDatabaseName);
}
