using FunctionApp.Postgres.Monitoring;

namespace FunctionApp.Postgres.Tests.Email.TestDoubles;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp.Tests/Email/TestDoubles/FakeNoodmailThrottleStore.cs</c> (#972 — port van
/// EmailProcessorFunction). Woordelijke kopie: in-memory dubbelganger van
/// <see cref="INoodmailThrottleStore"/> voor tests.
/// </summary>
internal sealed class FakeNoodmailThrottleStore : INoodmailThrottleStore
{
    private readonly Dictionary<string, DateTime> _verstuurdOp = new();

    public Task<DateTime?> LaatsteKeerVerstuurdAsync(string sleutel)
        => Task.FromResult(_verstuurdOp.TryGetValue(sleutel, out var waarde) ? waarde : (DateTime?)null);

    public Task RegistreerVerstuurdAsync(string sleutel, DateTime verstuurdOpUtc)
    {
        _verstuurdOp[sleutel] = verstuurdOpUtc;
        return Task.CompletedTask;
    }

    public Task WisAsync(string sleutel)
    {
        _verstuurdOp.Remove(sleutel);
        return Task.CompletedTask;
    }
}
