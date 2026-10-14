using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Omhulseltest van de Postgres-poort van de e-mailverwerker (#1716 slice 9). De fase-orkestratie en de
/// noodmails zijn tier-onafhankelijk getest in <c>Planner.Endpoints.Tests/Email/</c>; hier alleen dat deze
/// tier zijn eigen Graph-adapter, voorfilter, AI-gedrag en noodmailteksten correct aansluit.
/// </summary>
public class EmailProcessorFasenTests
{
    private static InkomendBericht Bericht(string id, string afzender = "trainer@voorbeeld.nl")
        => new() { MessageId = id, Afzender = afzender, Onderwerp = "Test" };

    private static EmailVerwerkerPoort Poort(FakeEmailGraphService graph, FakeNoodmailThrottleStore? throttle = null)
        => new(NullLogger.Instance, Mock.Of<IServiceProvider>(), NullLoggerFactory.Instance, graph,
            throttle ?? new FakeNoodmailThrottleStore(), "Host=niet-gebruikt");

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
    public void BereidAiVoor_ZonderChatClient_StoptZonderTeGooien()
        => Poort(new FakeEmailGraphService()).BereidAiVoor().Should().BeFalse(
            "buiten productie is IChatClient onvoorwaardelijk null (#857); de poll stopt zonder fout");

    [Fact]
    public async Task Noodmails_BevattenDeAdviesregelsVanDezeTier()
    {
        var graph = new FakeEmailGraphService();
        var poort = Poort(graph);

        await EmailNoodmailMelding.StuurDatabaseNoodmailAsync(graph, 1, "test", poort.Throttle, poort.NoodmailTeksten, NullLogger.Instance);
        await EmailNoodmailMelding.StuurOpenAiNoodmailAsync(graph, "quota", poort.Throttle, poort.NoodmailTeksten, NullLogger.Instance);

        graph.SentReplies.Should().HaveCount(2);
        graph.SentReplies[0].Body.Should().EndWith("Controleer de Postgres-hosting-omgeving (bijv. het Supabase-dashboard) op status en resource-limieten.");
        graph.SentReplies[1].Body.Should().Contain("Acties:\n  • Controleer de OpenAI-dashboard-quota\n  • Verhoog de quota-limiet of wacht tot de quota vernieuwt\n  • Als de quota");
    }

    [Fact]
    public void TierBericht_WerktMetDeGedeeldeKern()
    {
        InkomendBericht a = Bericht("a"), b = Bericht("b");

        EmailVerwerkerFasen.BepaalMislukteClassificaties([a, b], [(a, new BerichtClassificatie())], aiAborted: false)
            .Should().ContainSingle().Which.Should().BeSameAs(b);
    }
}
