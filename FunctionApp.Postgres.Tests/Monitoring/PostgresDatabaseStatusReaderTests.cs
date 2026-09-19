using AwesomeAssertions;
using FunctionApp.Postgres.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Monitoring;
using Xunit;

namespace FunctionApp.Postgres.Tests.Monitoring;

/// <summary>
/// Tests voor het terugvalpad van <see cref="PostgresDatabaseStatusReader"/> (#1268): de
/// verbindingsprobe. Die heeft geen draaiende database nodig om het interessante geval te bewijzen —
/// een poort waar niets luistert ís het scenario "database onbereikbaar".
///
/// <para>
/// De control-plane-tak (Management API) staat hier bewust niet in: die vereist een uitgaande
/// aanroep met een geheim en valt daarmee onder <c>EgressGuard</c>. Wat aan die tak besluitbaar is —
/// de vertaling van een statuswaarde naar een uitkomst — staat in <c>DatabaseUitvalCore</c> en is
/// daar getest.
/// </para>
/// </summary>
public class PostgresDatabaseStatusReaderTests
{
    /// <summary>
    /// Poort 1: daar luistert niets. Npgsql geeft dan een <c>NpgsqlException</c> — hetzelfde type als
    /// bij een echt onbereikbare database. Bewust zonder wachtwoord in de reeks: er komt nooit een
    /// verbinding tot stand, en een literal <c>Password=</c> in de repository is een patroon dat de
    /// secretscan van dit project terecht tegenhoudt.
    /// </summary>
    private const string OnbereikbareVerbinding =
        "Host=127.0.0.1;Port=1;Username=monitor;Database=sportlink;Timeout=2";

    /// <summary>Zonder echte wachttijd tussen de pogingen, zodat het herhaalgedrag testbaar is zonder
    /// dat de suite er seconden op staat te wachten.</summary>
    private static PostgresDatabaseStatusReader Reader(
        Func<string> verbindingsreeks, int pogingen = 2,
        ILogger<PostgresDatabaseStatusReader>? log = null)
        => new(log ?? NullLogger<PostgresDatabaseStatusReader>.Instance, pogingen, TimeSpan.Zero, verbindingsreeks);

    /// <summary>Telt de waarschuwingen, want elke mislukte poging logt er precies één. Het aantal
    /// pogingen is niet anders van buitenaf waar te nemen — de connectiereeks wordt één keer
    /// opgehaald en daarna hergebruikt.</summary>
    private sealed class TellendeLogger : ILogger<PostgresDatabaseStatusReader>
    {
        public int Waarschuwingen { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Waarschuwingen++;
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task OnbereikbareDatabase_GeeftUitgevallen()
    {
        var status = await Reader(() => OnbereikbareVerbinding).LeesStatusAsync();

        status.Beschikbaarheid.Should().Be(DatabaseBeschikbaarheid.Uitgevallen);
        status.Bron.Should().Contain("verbindingsprobe",
            "de noodmail moet laten zien dát dit een probe was en geen platformstatus");
    }

    /// <summary>
    /// Eén mislukte poging is te dun bewijs voor een URGENT-mail; het aantal pogingen is daarom geen
    /// detail maar de hele reden dat een netwerkhapering geen melding oplevert.
    /// </summary>
    [Fact]
    public async Task OnbereikbareDatabase_ProbeertHetIngestelde_AantalKeer()
    {
        var log = new TellendeLogger();

        await Reader(() => OnbereikbareVerbinding, pogingen: 3, log).LeesStatusAsync();

        log.Waarschuwingen.Should().Be(3);
    }

    /// <summary>
    /// Een onbruikbare of ontbrekende connectiereeks is een configuratiefout, geen databasestoring.
    /// Dit als uitval melden zou betekenen dat een verkeerd gezette app setting een URGENT-mail
    /// oplevert die de beheerder naar het verkeerde dashboard stuurt.
    /// </summary>
    [Fact]
    public async Task GeenBruikbareConnectiereeks_GeeftOnbepaaldEnGeenUitval()
    {
        var status = await Reader(() => throw new InvalidOperationException("niet gezet")).LeesStatusAsync();

        status.Beschikbaarheid.Should().Be(DatabaseBeschikbaarheid.Onbepaald);
    }

    /// <summary>
    /// De uitkomst mag geen host, gebruikersnaam of wachtwoord bevatten: hij belandt in het log en in
    /// de noodmail, en een hostnaam identificeert de club (CLAUDE.md regel 4a).
    /// </summary>
    [Fact]
    public async Task UitkomstBevatGeenVerbindingsgegevens()
    {
        var status = await Reader(() => OnbereikbareVerbinding).LeesStatusAsync();

        status.RuweStatus.Should().NotContain("127.0.0.1").And.NotContain("monitor");
        status.Bron.Should().NotContain("127.0.0.1").And.NotContain("monitor");
    }
}
