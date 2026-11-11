using AwesomeAssertions;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using FunctionApp.Postgres.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

public class EmailFewShotVoorfilterTests
{
    [Fact]
    public async Task OnbeschikbareVoorbeelden_StoppenVoorEigenUitgeslotenEnAutomatischeMailMutaties()
    {
        var graph = new FakeEmailGraphService();
        var batch = new List<InkomendBericht>
        {
            new() { MessageId = "own", Afzender = "mailbox@allstars-fc.test" },
            new() { MessageId = "excluded", Afzender = "blocked@allstars-fc.test" },
            new() { MessageId = "automatic", Afzender = "automatic@allstars-fc.test",
                InternetHeaders = [new("Auto-Submitted", "auto-generated")] },
            new() { MessageId = "classify", Afzender = "trainer@voorbeeld.nl" }
        };
        var poort = new Mock<IEmailVerwerkerPoort<InkomendBericht>>();
        poort.SetupGet(p => p.Graph).Returns(graph);
        poort.SetupGet(p => p.Throttle).Returns(new FakeNoodmailThrottleStore());
        poort.SetupGet(p => p.NoodmailTeksten).Returns(new EmailNoodmailTeksten("test", "test"));
        poort.Setup(p => p.HaalOngelezenAsync()).ReturnsAsync(batch);
        poort.Setup(p => p.BereidAiVoor()).Returns(true);
        poort.Setup(p => p.PreFilterAsync(It.IsAny<List<InkomendBericht>>(), It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>()))
            .Returns((List<InkomendBericht> emails, string _, IReadOnlySet<string> _) =>
                new EmailBatchFilterService().PreFilterVoorClassificatieAsync(emails, "mailbox@allstars-fc.test",
                    new HashSet<string> { "blocked@allstars-fc.test" }, graph, NullLogger.Instance));
        poort.Setup(p => p.WekEnLaadUitsluitingslijstAsync()).ReturnsAsync(new HashSet<string>());
        poort.Setup(p => p.FilterUitgesloten(It.IsAny<List<InkomendBericht>>(), It.IsAny<IReadOnlySet<string>>()))
            .Returns((List<InkomendBericht> emails, IReadOnlySet<string> _) => emails);
        poort.Setup(p => p.HaalFewShotVoorbeeldenOpAsync())
            .Returns(async () => (IReadOnlyList<ClassificatieCorrectieVoorbeeld>)await LearningMomentRepository.HaalVoorbeeldenOpAsync("Host=127.0.0.1;Port=1;Username=allstars;Timeout=1;Pooling=false", "ALLSTARS", NullLogger.Instance));

        await EmailVerwerkerFasen.VoerUitAsync(poort.Object, new UitsluitingslijstCache(), NullLogger.Instance);

        graph.ReadAttempts.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().BeEmpty();
        graph.CategoryUpdates.Should().BeEmpty();
        graph.EnsuredCategories.Should().BeEmpty();
        graph.TeamForwardings.Should().BeEmpty();
        graph.SentReplies.Should().ContainSingle(r => r.Subject.StartsWith("URGENT: Database niet bereikbaar", StringComparison.Ordinal));
        poort.Verify(p => p.BereidAiVoor(), Times.Once);
        poort.Verify(p => p.ClassificeerAsync(It.IsAny<List<InkomendBericht>>(),
            It.IsAny<IReadOnlyList<ClassificatieCorrectieVoorbeeld>>()), Times.Never);
        poort.Verify(p => p.PreFilterAsync(It.IsAny<List<InkomendBericht>>(), It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>()), Times.Never);
        poort.Verify(p => p.HaalFewShotVoorbeeldenOpAsync(), Times.Once);
    }
}
