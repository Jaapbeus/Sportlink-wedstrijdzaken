using Microsoft.Extensions.Logging;

namespace FunctionApp.Tests.Email.TestDoubles;

/// <summary>
/// Vangt elke logregel op (#1143) zodat een test kan bewijzen dat een bepaalde waarde — bijv. het
/// mailboxadres van een noodmail — nergens in een logregel verschijnt. Vangt zowel het
/// geformatteerde bericht als elke structured-logging placeholder-waarde apart op: een test die
/// alleen het samengevoegde bericht controleert zou een waarde die wél als state-veld is
/// meegegeven maar niet in de berichttekst zit, kunnen missen.
/// <para>
/// #1200 heeft daar de <b>exception-tekst</b> aan toegevoegd. Een <c>LogError(ex, ...)</c> geeft de
/// exception als apart veld mee aan de logprovider, dus een assertie over alleen template en
/// argumenten zou een waarde die uitsluitend in de exception staat niet zien. Ook het
/// <see cref="LogLevel"/> wordt nu bijgehouden: een "deze waarde staat in geen enkele logregel"-test
/// kan anders slagen doordat er helemaal niets is gelogd.
/// </para>
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    public List<string> Messages { get; } = new();

    /// <summary>Niveau van elke opgevangen logregel, in volgorde van loggen (#1200).</summary>
    public List<LogLevel> Levels { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Levels.Add(logLevel);
        Messages.Add(formatter(state, exception));

        if (state is IEnumerable<KeyValuePair<string, object>> velden)
        {
            foreach (var veld in velden)
            {
                if (veld.Value is not null)
                    Messages.Add(veld.Value.ToString() ?? "");
            }
        }

        if (exception is not null)
            Messages.Add(exception.ToString());
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
