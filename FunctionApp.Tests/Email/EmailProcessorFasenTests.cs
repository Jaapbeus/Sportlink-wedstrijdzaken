using AwesomeAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Planner.Shared.TeamResolution;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Omhulseltest van de SQL Server-poort van de e-mailverwerker (#1716 slice 9). De fase-orkestratie en de
/// noodmails zijn tier-onafhankelijk getest in <c>Planner.Endpoints.Tests/Email/</c>; hier alleen dat deze
/// tier zijn eigen Graph-adapter, voorfilter, AI-gedrag en noodmailteksten correct aansluit.
/// </summary>
public class EmailProcessorFasenTests
{
    private static InkomendBericht Bericht(string id, string afzender = "trainer@voorbeeld.nl")
        => new() { MessageId = id, Afzender = afzender, Onderwerp = "Test" };

    private static EmailVerwerkerPoort Poort(FakeEmailGraphService graph, FakeNoodmailThrottleStore? throttle = null)
        => new(NullLogger.Instance, Mock.Of<IServiceProvider>(), NullLoggerFactory.Instance, graph,
            Mock.Of<ITeamResolver>(), new RecordingEmailPersistenceService(), throttle ?? new FakeNoodmailThrottleStore());

    [Fact]
    public async Task Poll_UitgeslotenAfzender_MarkeertGelezenViaDeTierAdapterEnWektDeDatabaseNiet()
    {
        var graph = new FakeEmailGraphService();
        graph.UnreadEmails.Add(Bericht("m1", "uitgesloten@voorbeeld.nl"));
        var cache = new UitsluitingslijstCache();
        await cache.HerlaadAsync(
            () => Task.FromResult(new HashSet<string>(["uitgesloten@voorbeeld.nl"], StringComparer.OrdinalIgnoreCase)),
            DateTime.UtcNow);

        await EmailVerwerkerFasen.VoerUitAsync(Poort(graph), cache, NullLogger.Instance);

        graph.MarkedAsReadIds.Should().Equal(["m1"]);
        graph.SentReplies.Should().BeEmpty();
    }

    [Fact]
    public void BereidAiVoor_ZonderChatClient_GooitZoalsVoorheen()
    {
        var poort = Poort(new FakeEmailGraphService());

        poort.Invoking(p => p.BereidAiVoor()).Should().Throw<InvalidOperationException>()
            .WithMessage("IChatClient niet geconfigureerd*");
    }

    [Fact]
    public async Task Noodmails_BevattenDeAdviesregelsVanDezeTier()
    {
        var graph = new FakeEmailGraphService();
        var poort = Poort(graph);

        await EmailNoodmailMelding.StuurDatabaseNoodmailAsync(graph, 1, "test", poort.Throttle, poort.NoodmailTeksten, NullLogger.Instance);
        await EmailNoodmailMelding.StuurOpenAiNoodmailAsync(graph, "quota", poort.Throttle, poort.NoodmailTeksten, NullLogger.Instance);

        graph.SentReplies.Should().HaveCount(2);
        graph.SentReplies[0].Body.Should().Contain("Meest waarschijnlijke oorzaak: Azure SQL Serverless database was gepauzeerd")
            .And.EndWith("\"Continue using database with additional charges\"");
        graph.SentReplies[1].Body.Should().Contain("Acties:\n  • Controleer in Azure Portal → OpenAI resource → Overzicht → Quota\n"
            + "  • Verhoog de quota-limiet of wacht tot de quota vernieuwt (begin volgende maand)\n  • Als de quota");
    }

    [Fact]
    public void TierBericht_WerktMetDeGedeeldeKern()
    {
        InkomendBericht a = Bericht("a"), b = Bericht("b");

        EmailVerwerkerFasen.BepaalMislukteClassificaties([a, b], [(a, new BerichtClassificatie())], aiAborted: false)
            .Should().ContainSingle().Which.Should().BeSameAs(b);
    }
}
