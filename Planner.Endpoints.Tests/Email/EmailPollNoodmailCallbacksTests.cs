using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.Monitoring;
using Xunit;

namespace Planner.Endpoints.Tests.Email;

public sealed class EmailPollNoodmailCallbacksTests
{
    private static EmailVerwerkingsInstellingenLeesResultaat Snapshot(bool ingeschakeld = false) =>
        new(new(ingeschakeld, true, null, true, false), EmailVerwerkingsInstellingenLeesStatus.Beschikbaar);

    [Fact]
    public async Task Verbindingsuitval_MeldtEenmaalMetMetadataAantal_EnHerstelMaaktVolgendeMeldingMogelijk()
    {
        var stappen = new List<string>();
        var graph = new PollGraph(() => { stappen.Add("metadata"); return Task.FromResult(4); });
        var throttle = new FakeThrottle();
        using var services = new ServiceCollection().AddSingleton<INoodmailThrottlePoort>(_ =>
        {
            stappen.Add("throttle");
            return throttle;
        }).BuildServiceProvider();
        var verbindingBeschikbaar = false;

        Task<EmailVerwerkingsInstellingenLeesResultaat?> PollAsync() =>
            EmailPollNoodmailCallbacksFactory.LeesInstellingenAsync<INoodmailThrottlePoort>(
                graph, services,
                () =>
                {
                    stappen.Add("verbinding");
                    return verbindingBeschikbaar ? Task.CompletedTask
                        : Task.FromException(new DatabaseVerbindingOnbeschikbaarException());
                },
                () => false,
                () => { stappen.Add("instellingen"); return Task.CompletedTask; },
                () => { stappen.Add("snapshot"); return Task.FromResult(Snapshot()); },
                FakePoort.TestTeksten, NullLogger.Instance);

        await PollAsync();
        await PollAsync();
        stappen.Should().Equal("metadata", "verbinding", "throttle", "metadata", "verbinding");
        graph.Verzendingen.Verstuurd.Should().ContainSingle();
        graph.Verzendingen.Verstuurd[0].Body.Should().Contain("Ongelezen inboxitems (metadata): 4");
        (await throttle.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel)).Should().NotBeNull();

        verbindingBeschikbaar = true;
        await PollAsync();
        stappen.TakeLast(4).Should().Equal("metadata", "verbinding", "instellingen", "snapshot");
        (await throttle.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel)).Should().BeNull();

        verbindingBeschikbaar = false;
        await PollAsync();
        graph.Verzendingen.Verstuurd.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("geen-graph")]
    [InlineData("lege-inbox")]
    [InlineData("metadata-fout")]
    [InlineData("verbindingsconfiguratie-fout")]
    [InlineData("instellingen-fout")]
    [InlineData("schema-fout")]
    public async Task AndereStops_GeenValseNoodmail_EnGeenOpslagVoorMetadata(string stop)
    {
        var stappen = new List<string>();
        var graph = new PollGraph(() =>
        {
            stappen.Add("metadata");
            return stop == "metadata-fout" ? Task.FromException<int>(new InvalidOperationException("probe"))
                : Task.FromResult(stop == "lege-inbox" ? 0 : 2);
        });
        using var services = new ServiceCollection().AddSingleton<INoodmailThrottlePoort>(_ =>
        {
            stappen.Add("throttle");
            return new FakeThrottle();
        }).BuildServiceProvider();

        var resultaat = await EmailPollNoodmailCallbacksFactory.LeesInstellingenAsync<INoodmailThrottlePoort>(
            stop == "geen-graph" ? null : graph, services,
            () =>
            {
                stappen.Add("verbinding");
                return stop == "verbindingsconfiguratie-fout"
                    ? Task.FromException(new InvalidOperationException("configuration")) : Task.CompletedTask;
            },
            () => stop == "instellingen-fout",
            () => { stappen.Add("instellingen"); return Task.CompletedTask; },
            () =>
            {
                stappen.Add("snapshot");
                throw new InvalidOperationException("schema");
            }, FakePoort.TestTeksten, NullLogger.Instance);

        resultaat.Should().BeNull();
        graph.Verzendingen.Verstuurd.Should().BeEmpty();
        stappen.Should().Equal(stop switch
        {
            "geen-graph" => [],
            "lege-inbox" or "metadata-fout" => ["metadata"],
            "verbindingsconfiguratie-fout" => ["metadata", "verbinding"],
            "instellingen-fout" => ["metadata", "verbinding", "throttle", "instellingen"],
            _ => ["metadata", "verbinding", "throttle", "instellingen", "snapshot"]
        });
    }

    [Fact]
    public async Task Succes_GeeftVerseSnapshotDoor()
    {
        var verwacht = Snapshot(true);
        using var services = new ServiceCollection().AddSingleton<INoodmailThrottlePoort>(new FakeThrottle())
            .BuildServiceProvider();
        var resultaat = await EmailPollNoodmailCallbacksFactory.LeesInstellingenAsync<INoodmailThrottlePoort>(
            new PollGraph(() => Task.FromResult(2)), services, () => Task.CompletedTask, () => false,
            () => Task.CompletedTask, () => Task.FromResult(verwacht), FakePoort.TestTeksten, NullLogger.Instance);

        resultaat.Should().BeSameAs(verwacht);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OntbrekendeThrottleRegistratie_WordtGeenDatabaseUitval(bool verbindingsuitval)
    {
        var graph = new PollGraph(() => Task.FromResult(2));
        using var services = new ServiceCollection().BuildServiceProvider();
        var actie = () => EmailPollNoodmailCallbacksFactory.LeesInstellingenAsync<INoodmailThrottlePoort>(
            graph, services,
            () => verbindingsuitval ? Task.FromException(new DatabaseVerbindingOnbeschikbaarException())
                : Task.CompletedTask, () => false, () => Task.CompletedTask,
            () => Task.FromResult(Snapshot(true)), FakePoort.TestTeksten, NullLogger.Instance);

        await actie.Should().ThrowAsync<InvalidOperationException>();
        graph.Verzendingen.Verstuurd.Should().BeEmpty();
    }

    private sealed class PollGraph(Func<Task<int>> probe) : IEmailPollGraph
    {
        public FakeGraph Verzendingen { get; } = new();
        public Task<int> TelOngelezenInboxBerichtenAsync() => probe();
        public Task SendReplyAsync(string to, string subject, string body, string? conversationId,
            IReadOnlyList<string>? bcc = null, EmailBijlage? bijlage = null) =>
            Verzendingen.SendReplyAsync(to, subject, body, conversationId, bcc, bijlage);
        public Task SetCategoriesAsync(string messageId, params string[] categories) => throw new InvalidOperationException();
        public Task EnsureMasterCategoryAsync(string name, string colorPreset) => throw new InvalidOperationException();
        public Task MarkAsReadAsync(string messageId) => throw new InvalidOperationException();
    }
}
