using SportlinkFunction.Monitoring;

namespace FunctionApp.Tests.Monitoring.TestDoubles;

internal sealed class FakeDatabaseStatusReader : IDatabaseStatusReader
{
    public DatabaseStatusInfo? StatusToReturn { get; set; }
    public Exception? ExceptionToThrow { get; set; }
    public int AantalAanroepen { get; private set; }

    public Task<DatabaseStatusInfo> LeesStatusAsync(
        string subscriptionId, string resourceGroup, string sqlServerName, string sqlDatabaseName)
    {
        AantalAanroepen++;
        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        return Task.FromResult(StatusToReturn ?? new DatabaseStatusInfo("Online", null));
    }
}
