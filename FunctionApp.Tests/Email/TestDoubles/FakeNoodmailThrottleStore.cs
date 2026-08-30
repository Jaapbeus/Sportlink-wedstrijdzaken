using SportlinkFunction.Monitoring;

namespace FunctionApp.Tests.Email.TestDoubles;

/// <summary>
/// In-memory dubbelganger van <see cref="INoodmailThrottleStore"/> voor tests. Bewijst dat het
/// throttle-gedrag correct werkt zolang de staat maar buiten een static veld op de functieklasse leeft
/// (#831) — een echte cold start wordt hiermee niet gesimuleerd (dat vereist Azurite/Table Storage, zie
/// de handmatige verificatie in de PR), maar de architecturale fix (staat via een geïnjecteerde
/// afhankelijkheid i.p.v. procesgeheugen) is hiermee wel te bewijzen.
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
