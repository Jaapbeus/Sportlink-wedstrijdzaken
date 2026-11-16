using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.Monitoring;
using Xunit;

namespace Planner.Endpoints.Tests.Email;

public sealed class EmailPollNoodmailCallbacksTests
{
    private static EmailVerwerkingsInstellingenLeesResultaat Uitgeschakeld() =>
        new(new(false, true, null, true, false), EmailVerwerkingsInstellingenLeesStatus.Beschikbaar);

    [Fact]
    public async Task Verbindingsuitval_MeldtEenmaalMetMetadataAantal_EnHerstelMaaktVolgendeMeldingMogelijk()
    {
        var graph = new FakeGraph();
        var throttle = new FakeThrottle();
        var callbacks = EmailPollNoodmailCallbacksFactory.Maak(graph, throttle, FakePoort.TestTeksten,
            NullLogger.Instance, "Ongelezen inboxitems (metadata)");
        var stappen = new List<string>();
        var verbindingBeschikbaar = false;

        async Task PollAsync()
        {
            await EmailPollVoorbereiding.LeesInstellingenAsync(
                () => { stappen.Add("metadata"); return Task.FromResult(4); },
                () =>
                {
                    stappen.Add("verbinding");
                    return verbindingBeschikbaar ? Task.CompletedTask
                        : Task.FromException(new DatabaseVerbindingOnbeschikbaarException());
                },
                () => false,
                () => { stappen.Add("instellingen"); return Task.CompletedTask; },
                () => { stappen.Add("snapshot"); return Task.FromResult(Uitgeschakeld()); },
                NullLogger.Instance, callbacks.BijVerbindingsfout, callbacks.BijHerstel);
        }

        await PollAsync();
        await PollAsync();
        stappen.Should().Equal("metadata", "verbinding", "metadata", "verbinding");
        graph.Verstuurd.Should().ContainSingle();
        graph.Verstuurd[0].Body.Should().Contain("Ongelezen inboxitems (metadata): 4");
        (await throttle.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel)).Should().NotBeNull();

        verbindingBeschikbaar = true;
        await PollAsync();
        stappen.TakeLast(4).Should().Equal("metadata", "verbinding", "instellingen", "snapshot");
        (await throttle.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel)).Should().BeNull();

        verbindingBeschikbaar = false;
        await PollAsync();
        graph.Verstuurd.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("geen-graph")]
    [InlineData("lege-inbox")]
    [InlineData("metadata-fout")]
    [InlineData("instellingen-fout")]
    public async Task AndereStops_VeroorzakenGeenValseDatabaseNoodmail(string stop)
    {
        var graph = new FakeGraph();
        var callbacks = EmailPollNoodmailCallbacksFactory.Maak(graph, new FakeThrottle(),
            FakePoort.TestTeksten, NullLogger.Instance, "Ongelezen inboxitems (metadata)");
        var stappen = new List<string>();
        Func<Task<int>>? probe = stop == "geen-graph" ? null : () =>
        {
            stappen.Add("metadata");
            return stop == "metadata-fout" ? Task.FromException<int>(new InvalidOperationException("probe"))
                : Task.FromResult(stop == "lege-inbox" ? 0 : 2);
        };

        await EmailPollVoorbereiding.LeesInstellingenAsync(probe,
            () => { stappen.Add("verbinding"); return Task.CompletedTask; },
            () => stop == "instellingen-fout",
            () => { stappen.Add("instellingen"); return Task.CompletedTask; },
            () => { stappen.Add("snapshot"); return Task.FromResult(Uitgeschakeld()); },
            NullLogger.Instance, callbacks.BijVerbindingsfout, callbacks.BijHerstel);

        graph.Verstuurd.Should().BeEmpty();
        stappen.Should().Equal(stop == "geen-graph" ? [] : stop is "lege-inbox" or "metadata-fout"
            ? ["metadata"] : ["metadata", "verbinding", "instellingen"]);
    }
}
