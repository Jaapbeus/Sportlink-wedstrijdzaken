using Microsoft.Extensions.Logging;

namespace FunctionApp.Tests.Email.TestDoubles;

/// <summary>
/// Vangt elke logregel op (#1143) zodat een test kan bewijzen dat een bepaalde waarde — bijv. het
/// mailboxadres van een noodmail — nergens in een logregel verschijnt. Vangt zowel het
/// geformatteerde bericht als elke structured-logging placeholder-waarde apart op: een test die
/// alleen het samengevoegde bericht controleert zou een waarde die wél als state-veld is
/// meegegeven maar niet in de berichttekst zit, kunnen missen.
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    public List<string> Messages { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));

        if (state is IEnumerable<KeyValuePair<string, object>> velden)
        {
            foreach (var veld in velden)
            {
                if (veld.Value is not null)
                    Messages.Add(veld.Value.ToString() ?? "");
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
