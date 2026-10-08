using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Sync;
using SportlinkFunction;
using Xunit;

namespace FunctionApp.Tests.Sync;

/// <summary>
/// Review #1558 R1-F1: een ontbrekende of lege teams-respons is geen bewijs dat Sportlink geen enkel
/// team meer kent. Zo'n respons moet als mislukte fetch tellen, zodat de reconciliatie bestaande teams
/// behoudt. Alleen een lijst met minstens één team geldt als complete snapshot.
/// </summary>
public class TeamsSnapshotReconciliatieTests
{
    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[]")]
    public async Task OntbrekendeOfLegeTeamsRespons_IsGeenCompleteSnapshot(string body)
    {
        using var client = new HttpClient(new StubHandler(body));

        var volledig = await SportlinkSyncPipeline.FetchAndStoreTeamsAsync(
            "http://fixture.invalid/teams", "testclub", NullLogger.Instance, client);

        volledig.Should().BeFalse("anders markeert de reconciliatie alle bestaande teams als verdwenen");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(25, true)]
    public void IsVolledigeTeamsSnapshot_VereistMinstensEenTeam(int? aantal, bool verwacht)
        => ReconciliatieOndergrens.IsVolledigeTeamsSnapshot(aantal).Should().Be(verwacht);
}
