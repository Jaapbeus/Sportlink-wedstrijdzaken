using FluentAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using FunctionApp.Tests.Monitoring.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Monitoring;
using Xunit;

namespace FunctionApp.Tests.Monitoring;

/// <summary>
/// Tests voor de kernlogica van de onafhankelijke database-uitvalmonitor (#831). Deze functie bestaat
/// omdat de bestaande, e-mail-pipeline-afhankelijke noodmail in <c>EmailProcessorFunction</c> alleen
/// wordt gecontroleerd als er inkomende e-mail is die fase 2 bereikt — tijdens de 5+ dagen durende
/// uitval van 25-30 augustus 2026 (#799/#808) bleek dát de eigenlijke reden voor "geen enkele melding".
/// </summary>
public class DatabaseUitvalMonitorFunctionTests
{
    private static readonly DateTime Nu = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    private static Task RunAsync(
        FakeDatabaseStatusReader reader, FakeNoodmailThrottleStore store, FakeEmailGraphService graph, DateTime nuUtc)
        => DatabaseUitvalMonitorFunction.VerwerkStatusAsync(
            reader, store, graph,
            subscriptionId: "sub", resourceGroup: "rg", sqlServerName: "server", sqlDatabaseName: "db",
            nuUtc, NullLogger.Instance);

    [Fact]
    public async Task Online_StuurtGeenMeldingEnRegistreertNiets()
    {
        var reader = new FakeDatabaseStatusReader { StatusToReturn = new DatabaseStatusInfo("Online", null) };
        var store = new FakeNoodmailThrottleStore();
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        graph.SentReplies.Should().BeEmpty();
        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalMonitorFunction.ThrottleSleutel)).Should().BeNull();
    }

    [Fact]
    public async Task Online_MetOpenstaandeRegistratie_WistDieRegistratie()
    {
        var reader = new FakeDatabaseStatusReader { StatusToReturn = new DatabaseStatusInfo("Online", null) };
        var store = new FakeNoodmailThrottleStore();
        await store.RegistreerVerstuurdAsync(DatabaseUitvalMonitorFunction.ThrottleSleutel, Nu.AddDays(-1));
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        graph.SentReplies.Should().BeEmpty();
        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalMonitorFunction.ThrottleSleutel)).Should().BeNull();
    }

    [Fact]
    public async Task KortePauze_BinnenNormaleMarge_StuurtGeenMelding()
    {
        var reader = new FakeDatabaseStatusReader
        {
            StatusToReturn = new DatabaseStatusInfo("Paused", Nu - TimeSpan.FromHours(1))
        };
        var store = new FakeNoodmailThrottleStore();
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        graph.SentReplies.Should().BeEmpty("een pauze van 1 uur is normaal auto-pause-gedrag, geen structurele uitval");
    }

    [Fact]
    public async Task LangePauze_ZonderEerdereMelding_StuurtMeldingEnRegistreert()
    {
        var reader = new FakeDatabaseStatusReader
        {
            StatusToReturn = new DatabaseStatusInfo("Paused", Nu - TimeSpan.FromHours(10))
        };
        var store = new FakeNoodmailThrottleStore();
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        graph.SentReplies.Should().ContainSingle();
        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalMonitorFunction.ThrottleSleutel)).Should().Be(Nu);
    }

    [Fact]
    public async Task LangePauze_MetRecenteMelding_StuurtNietOpnieuw()
    {
        var reader = new FakeDatabaseStatusReader
        {
            StatusToReturn = new DatabaseStatusInfo("Paused", Nu - TimeSpan.FromHours(30))
        };
        var store = new FakeNoodmailThrottleStore();
        await store.RegistreerVerstuurdAsync(DatabaseUitvalMonitorFunction.ThrottleSleutel, Nu.AddHours(-5));
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        graph.SentReplies.Should().BeEmpty();
    }

    /// <summary>
    /// Tijdens een langdurige, meerdaagse uitval moet de dagelijkse monitor wél opnieuw melden zodra
    /// het herhalingsvenster verstreken is — dit is de "dagelijkse herinnering"-eigenschap die de
    /// onvoorwaardelijke suppressie in EmailProcessorFunction bewust niet heeft.
    /// </summary>
    [Fact]
    public async Task LangePauze_MetVerlopenMelding_StuurtOpnieuwEnWerktRegistratieBij()
    {
        var reader = new FakeDatabaseStatusReader
        {
            StatusToReturn = new DatabaseStatusInfo("Paused", Nu - TimeSpan.FromDays(3))
        };
        var store = new FakeNoodmailThrottleStore();
        await store.RegistreerVerstuurdAsync(DatabaseUitvalMonitorFunction.ThrottleSleutel, Nu.AddHours(-25));
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        graph.SentReplies.Should().ContainSingle();
        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalMonitorFunction.ThrottleSleutel)).Should().Be(Nu);
    }

    [Fact]
    public async Task Gepauzeerd_ZonderPausedDate_StuurtGeenMelding()
    {
        // Fail-safe: zonder betrouwbare duur liever stil blijven dan een fout-positief op een normale,
        // korte auto-pause.
        var reader = new FakeDatabaseStatusReader { StatusToReturn = new DatabaseStatusInfo("Paused", null) };
        var store = new FakeNoodmailThrottleStore();
        var graph = new FakeEmailGraphService();

        await RunAsync(reader, store, graph, Nu);

        graph.SentReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task StatusReaderGooitException_LogtEnCrasht_MaarStuurtGeenMelding()
    {
        var reader = new FakeDatabaseStatusReader { ExceptionToThrow = new HttpRequestException("ARM 503") };
        var store = new FakeNoodmailThrottleStore();
        var graph = new FakeEmailGraphService();

        var act = async () => await RunAsync(reader, store, graph, Nu);

        await act.Should().NotThrowAsync();
        graph.SentReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task VerzendfoutMislukt_RegistreertNietZodatVolgendeControleHetOpnieuwProbeert()
    {
        var reader = new FakeDatabaseStatusReader
        {
            StatusToReturn = new DatabaseStatusInfo("Paused", Nu - TimeSpan.FromHours(10))
        };
        var store = new FakeNoodmailThrottleStore();
        var graph = new FakeEmailGraphService { ThrowOnSendReply = true };

        await RunAsync(reader, store, graph, Nu);

        (await store.LaatsteKeerVerstuurdAsync(DatabaseUitvalMonitorFunction.ThrottleSleutel)).Should().BeNull();
    }
}
