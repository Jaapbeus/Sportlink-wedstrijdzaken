using AwesomeAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public class EmailBatchFilterServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreFilter_LeesFoutBlijftPerBericht_EnRestGaatNaarClassificatie(bool eigen)
    {
        var graph = new FakeEmailGraphService();
        graph.FailingReadIds.Add("failed");
        var logger = new RecordingLogger();
        var service = new EmailBatchFilterService();
        var emails = new List<InkomendBericht>
        {
            new() { MessageId = "failed", Afzender = eigen ? "mailbox@allstars-fc.test" : "blocked@allstars-fc.test" },
            new() { MessageId = "ok-filtered", Afzender = "mailbox@allstars-fc.test" },
            new() { MessageId = "classify", Afzender = "trainer@voorbeeld.nl" }
        };

        var result = await service.PreFilterVoorClassificatieAsync(emails, "mailbox@allstars-fc.test",
            new HashSet<string> { "blocked@allstars-fc.test" }, graph, logger);

        result.Should().ContainSingle(e => e.MessageId == "classify");
        var classifiedIds = new List<string>();
        var classified = await new EmailClassificationService().ClassificeerBatchAsync(result, (email, _) =>
        {
            classifiedIds.Add(email.MessageId);
            return Task.FromResult(new BerichtClassificatie { Type = VerzoekType.BuitenScope });
        }, _ => false, logger);
        classifiedIds.Should().Equal("classify");
        classified.Classificaties.Should().ContainSingle();
        graph.ReadAttempts.Should().Equal("failed", "ok-filtered");
        graph.MarkedAsReadIds.Should().Equal("ok-filtered");
        graph.SentReplies.Should().BeEmpty();
        graph.TeamForwardings.Should().BeEmpty();
        logger.Messages.Should().Contain("Gefilterd bericht kon niet als gelezen worden gemarkeerd; overige berichten worden verwerkt.");
        logger.Messages.Should().NotContain(m => m.Contains("synthetic Graph failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreFilterVoorClassificatie_FiltertEigenMailboxEnUitsluitingslijst()
    {
        var service = new EmailBatchFilterService();
        var graph = new FakeEmailGraphService();
        var emails = new List<InkomendBericht>
        {
            new() { MessageId = "m1", Afzender = "coordinator@club.nl" },
            new() { MessageId = "m2", Afzender = "blocked@club.nl" },
            new() { MessageId = "m3", Afzender = "tegenstander@andereclub.nl" }
        };
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked@club.nl" };

        var result = await service.PreFilterVoorClassificatieAsync(
            emails,
            "coordinator@club.nl",
            excluded,
            graph,
            NullLogger.Instance);

        result.Should().ContainSingle(e => e.MessageId == "m3");
        graph.MarkedAsReadIds.Should().BeEquivalentTo(["m1", "m2"]);
    }

    [Fact]
    public void FilterUitgeslotenAdressen_VerwijdertAlleGeblokkeerdeAfzenders()
    {
        var service = new EmailBatchFilterService();
        var emails = new List<InkomendBericht>
        {
            new() { MessageId = "m1", Afzender = "a@x.nl" },
            new() { MessageId = "m2", Afzender = "b@x.nl" },
            new() { MessageId = "m3", Afzender = "a@x.nl" }
        };
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a@x.nl" };

        var result = service.FilterUitgeslotenAdressen(emails, excluded);

        result.Select(e => e.MessageId).Should().Equal(["m2"]);
    }

    [Fact]
    public async Task LabelBuitenScopeAsync_LabeltAlleenBuitenScopeBerichten()
    {
        var service = new EmailBatchFilterService();
        var graph = new FakeEmailGraphService();
        var items = new List<(InkomendBericht Email, BerichtClassificatie Classificatie)>
        {
            (new InkomendBericht { MessageId = "m1" }, new BerichtClassificatie { Type = VerzoekType.BuitenScope }),
            (new InkomendBericht { MessageId = "m2" }, new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek })
        };

        await service.LabelBuitenScopeAsync(items, graph, NullLogger.Instance);

        graph.CategoryUpdates.Should().ContainSingle(c => c.MessageId == "m1");
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1");
        graph.EnsuredCategories.Should().ContainSingle(c => c.Name == "Geen AI antwoord");
    }

    [Fact]
    public async Task LabelBuitenScopeAsync_BijCategorieFout_LogtGeenSuccesEnMarkeertNietAlsGelezen()
    {
        var service = new EmailBatchFilterService();
        var graph = new FakeEmailGraphService
        {
            ExceptionToThrowOnSetCategories = new InvalidOperationException("synthetic Graph failure")
        };
        var logger = new RecordingLogger();
        var items = new[]
        {
            (new InkomendBericht { MessageId = "synthetic-message-id" },
                new BerichtClassificatie { Type = VerzoekType.BuitenScope })
        };

        await service.LabelBuitenScopeAsync(items, graph, logger);

        graph.MarkedAsReadIds.Should().BeEmpty();
        logger.Messages.Should().Contain(message => message.Contains("Fout bij Outlook-labeling BuitenScope", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(message => message.Contains("gelabeld in Outlook", StringComparison.Ordinal));
    }
}
