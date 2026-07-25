using FluentAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

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
