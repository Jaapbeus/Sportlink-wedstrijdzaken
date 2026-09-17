using Microsoft.Extensions.Logging;

namespace FunctionApp.Postgres.Tests.Email.TestDoubles;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp.Tests/Email/TestDoubles/RecordingLogger.cs</c>
/// (#1143). Woordelijke kopie: vangt elke logregel op zodat een test kan bewijzen dat een bepaalde
/// waarde — bijv. het mailboxadres van een noodmail — nergens in een logregel verschijnt.
/// <para>
/// #1200: vangt ook de exception-tekst en het <see cref="LogLevel"/> op, zodat een assertie zowel
/// de template, de argumenten als de exception dekt én kan eisen dát er iets gelogd is.
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
