using AwesomeAssertions;
using FunctionApp.Postgres.Monitoring;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Monitoring;
using Xunit;

namespace FunctionApp.Postgres.Tests.Monitoring;

/// <summary>
/// Tests voor het deel van de uitvalmonitor dat níét gedeeld is (#1268): het zelf bijhouden sinds
/// wanneer de uitval loopt. De beslisregels zelf staan in <c>Planner.Shared.Tests</c> —
/// <c>DatabaseUitvalCoreTests</c>; die worden hier bewust niet nog eens nagebouwd.
///
/// <para>
/// Waarom dit een eigen suite verdient: de hostingomgeving van deze tier levert alleen een status en
/// geen tijdstip van uitvallen. Het startmoment komt dus uit de opslag van deze monitor, en de
/// volgorde waarin dat wordt gelezen, vastgelegd en weer gewist is niet af te lezen aan de
/// eindtoestand van één run.
/// </para>
/// </summary>
public class DatabaseUitvalMonitorFunctionTests
{
    private static readonly DateTime Nu = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeStatusReader : IDatabaseStatusReader
    {
        public DatabaseStatusInfo Status { get; init; } =
            new("ACTIVE_HEALTHY", DatabaseBeschikbaarheid.Beschikbaar, null);

        public Exception? ExceptionToThrow { get; init; }

        public Task<DatabaseStatusInfo> LeesStatusAsync(CancellationToken annuleringstoken = default)
            => ExceptionToThrow is not null
                ? throw ExceptionToThrow
                : Task.FromResult(Status);
    }

    private static DatabaseStatusInfo Uitgevallen(string ruweStatus = "INACTIVE")
        => new(ruweStatus, DatabaseUitvalCore.BepaalBeheerdePostgresBeschikbaarheid(ruweStatus), null);

    private static Task RunAsync(
        FakeStatusReader reader, FakeNoodmailThrottleStore store, FakeEmailGraphService? graph, DateTime nuUtc)
        => DatabaseUitvalMonitorFunction.VerwerkStatusAsync(reader, store, graph, nuUtc, NullLogger.Instance);

    [Fact]
    public async Task EersteWaarnemingVanUitval_LegtStartmomentVastEnMeldtDirect()
    {
        var reader = new FakeStatusReader { Status = Uitgevallen() };
        var store = new FakeNoodmailThrottleStore();
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel)).Should().Be(Nu);
        graph.SentReplies.Should().ContainSingle(
            "op deze tier is er geen routinematige auto-pause om op te wachten");
        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.NoodmailSleutel)).Should().Be(Nu);
    }

    /// <summary>
    /// De kern van het eigen bijhouden: een tweede run tijdens dezelfde uitval mag het startmoment
    /// niet verzetten. Zou hij dat wel doen, dan blijft de gemelde duur eeuwig nul en zou een
    /// drempel-gebaseerde melding nooit afgaan.
    /// </summary>
    [Fact]
    public async Task TweedeRunTijdensZelfdeUitval_HoudtHetOorspronkelijkeStartmomentAan()
    {
        var reader = new FakeStatusReader { Status = Uitgevallen() };
        var store = new FakeNoodmailThrottleStore();
        var eersteWaarneming = Nu.AddDays(-2);
        await store.RegistreerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel, eersteWaarneming);
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel))
            .Should().Be(eersteWaarneming);
        graph.SentReplies.Should().ContainSingle();
        graph.SentReplies[0].Body.Should().Contain("al circa 48 uur");
    }

    [Fact]
    public async Task HerstelNaUitval_WistZowelDeMeldingAlsDeEersteWaarneming()
    {
        var reader = new FakeStatusReader();
        var store = new FakeNoodmailThrottleStore();
        await store.RegistreerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel, Nu.AddDays(-2));
        await store.RegistreerVerstuurdAsync(DatabaseUitvalCore.NoodmailSleutel, Nu.AddDays(-1));
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel)).Should().BeNull();
        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.NoodmailSleutel)).Should().BeNull();
        graph.SentReplies.Should().BeEmpty();
    }

    /// <summary>
    /// Een tussentoestand (opstarten, herstarten, upgraden) mag de lopende registratie niet wissen —
    /// anders begint de duurtelling opnieuw zodra het platform één keer "RESTARTING" rapporteert, en
    /// haalt een echte, meerdaagse uitval nooit meer een tweede melding.
    /// </summary>
    [Fact]
    public async Task Tussentoestand_LaatEersteWaarnemingStaanEnMeldtNiet()
    {
        var reader = new FakeStatusReader
        {
            Status = new DatabaseStatusInfo(
                "RESTARTING", DatabaseBeschikbaarheid.Onbepaald, null),
        };
        var store = new FakeNoodmailThrottleStore();
        var eersteWaarneming = Nu.AddDays(-1);
        await store.RegistreerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel, eersteWaarneming);
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel))
            .Should().Be(eersteWaarneming);
        graph.SentReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task StatusNietVastTeStellen_MeldtNietsEnCrashtNiet()
    {
        var reader = new FakeStatusReader { ExceptionToThrow = new HttpRequestException("503") };
        var store = new FakeNoodmailThrottleStore();
        var graph = new FakeEmailGraphService();

        var act = async () => await RunAsync(reader, store, graph, Nu);

        await act.Should().NotThrowAsync("een kapotte controle is geen databasestoring");
        graph.SentReplies.Should().BeEmpty();
        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel)).Should().BeNull();
    }

    [Fact]
    public async Task VerzendfoutMislukt_RegistreertNietZodatVolgendeRunHetOpnieuwProbeert()
    {
        var reader = new FakeStatusReader { Status = Uitgevallen() };
        var store = new FakeNoodmailThrottleStore();
        var graph = new FakeEmailGraphService { ThrowOnSendReply = true };

        await RunAsync(reader, store, graph, Nu);

        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.NoodmailSleutel)).Should().BeNull();
    }

    /// <summary>
    /// Zonder Graph-registratie (lokaal, of een club zonder e-mailkoppeling) blijft de uitval wél
    /// vastgelegd, zodat de duur klopt zodra er weer verstuurd kan worden — maar er wordt niets
    /// geregistreerd als "gemeld".
    /// </summary>
    [Fact]
    public async Task ZonderGraphService_LegtUitvalVastMaarRegistreertGeenMelding()
    {
        var reader = new FakeStatusReader { Status = Uitgevallen() };
        var store = new FakeNoodmailThrottleStore();

        await RunAsync(reader, store, graph: null, Nu);

        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.EersteWaarnemingSleutel)).Should().Be(Nu);
        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalCore.NoodmailSleutel)).Should().BeNull();
    }

    /// <summary>
    /// De noodmail mag geen waarde bevatten die de club identificeert (CLAUDE.md regel 4a) en geen
    /// e-mailadres (SECURITY.md Laag 5).
    /// </summary>
    [Fact]
    public async Task Noodmail_BevatGeenAdresEnGeenProjectidentificatie()
    {
        Environment.SetEnvironmentVariable("SUPABASE_PROJECT_REF", "abcdefghijklmnopqrst");
        Environment.SetEnvironmentVariable("GraphMailbox", "uitvalmonitor@voorbeeld.nl");
        try
        {
            var reader = new FakeStatusReader { Status = Uitgevallen() };
            var store = new FakeNoodmailThrottleStore();
            var graph = new FakeEmailGraphService();

            await RunAsync(reader, store, graph, Nu);

            var body = graph.SentReplies.Should().ContainSingle().Subject.Body;
            body.Should().NotContain("abcdefghijklmnopqrst");
            body.Should().NotContain("@");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPABASE_PROJECT_REF", null);
            Environment.SetEnvironmentVariable("GraphMailbox", null);
        }
    }
}
