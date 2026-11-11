using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp.Tests/Email/EmailBatchFilterServiceTests.cs</c> (#972 — port van
/// EmailProcessorFunction). Woordelijke kopie.
/// </summary>
public class EmailBatchFilterServiceTests
{
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
    public async Task PreFilterVoorClassificatie_EigenNoodmailMetUrgentPrefixBlijftOngelezen()
    {
        var service = new EmailBatchFilterService();
        var graph = new FakeEmailGraphService();
        var emails = new List<InkomendBericht>
        {
            new() { MessageId = "own-alert", Afzender = "trainer@voorbeeld.nl", Onderwerp = "URGENT: Database niet bereikbaar" },
            new() { MessageId = "own-alert-lowercase", Afzender = "trainer@voorbeeld.nl", Onderwerp = "urgent: OpenAI quota overschreden" },
            new() { MessageId = "external-alert", Afzender = "trainer@allstars-fc.test", Onderwerp = "URGENT: not an internal alert" }
        };

        var result = await service.PreFilterVoorClassificatieAsync(
            emails,
            "trainer@voorbeeld.nl",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            graph,
            NullLogger.Instance);

        result.Should().ContainSingle().Which.MessageId.Should().Be("external-alert");
        graph.MarkAsReadCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task PreFilterVoorClassificatie_EigenMailboxMetBijnaUrgentOnderwerp_MarkAsRead()
    {
        var service = new EmailBatchFilterService();
        var graph = new FakeEmailGraphService();

        var result = await service.PreFilterVoorClassificatieAsync(
            [new InkomendBericht { MessageId = "own-mail", Afzender = "trainer@voorbeeld.nl", Onderwerp = " URGENT: not an exact prefix" }],
            "trainer@voorbeeld.nl",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            graph,
            NullLogger.Instance);

        result.Should().BeEmpty();
        graph.MarkAsReadCalls.Should().ContainSingle().Which.Should().Be("own-mail");
        graph.MarkedAsReadIds.Should().ContainSingle().Which.Should().Be("own-mail");
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
}
