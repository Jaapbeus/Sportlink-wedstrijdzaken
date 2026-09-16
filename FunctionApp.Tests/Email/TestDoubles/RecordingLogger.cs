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

    /// <summary>
    /// Het geformatteerde bericht mét zijn niveau (#1201). <see cref="Messages"/> bevat óók de losse
    /// placeholder-waarden en kan daardoor niet zeggen op welk niveau iets gelogd is; een test die
    /// wil bewijzen dat een waarschuwing daadwerkelijk is gelogd (en de melding dus niet stilzwijgend
    /// verdwenen is toen het adres eruit ging) heeft dat onderscheid nodig.
    /// </summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    /// <summary>Alleen de niveaus, in volgorde van loggen — afgeleid van <see cref="Entries"/> (#1200).</summary>
    public List<LogLevel> Levels => Entries.ConvertAll(e => e.Level);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
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
