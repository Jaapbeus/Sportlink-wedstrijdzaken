using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Planner.Shared.Tests.Email.TestDoubles;
using Xunit;

namespace Planner.Shared.Tests.Email;

public sealed class EmailBatchFilterKernTests
{
    [Fact]
    public async Task PreFilter_BehoudtVolgordeEnFiltertMailboxUitsluitingenEnAutomatischeAfzenders()
    {
        var graph = new FakeReplyGraph();
        var mailbox = new RecordingMailbox(graph);
        var emails = new[]
        {
            new Bericht("own", "OWN@allstars-fc.test"),
            new Bericht("excluded", "blocked@allstars-fc.test"),
            new Bericht("automatic", "auto@allstars-fc.test", [new(AutomatischeAfzenderHerkenning.AutoSubmitted, "auto-replied")]),
            new Bericht("normal", "person@allstars-fc.test")
        };

        var result = await EmailBatchFilterKern.PreFilterVoorClassificatieAsync(
            emails, "own@allstars-fc.test", new HashSet<string> { "blocked@allstars-fc.test" },
            email => email.Headers, graph, mailbox, NullLogger.Instance);

        result.Should().ContainSingle().Which.MessageId.Should().Be("normal");
        graph.MarkedAsReadIds.Should().Equal("own", "excluded", "automatic");
        graph.EnsuredCategories.Should().ContainSingle();
        graph.CategoryUpdates.Should().ContainSingle().Which.MessageId.Should().Be("automatic");
    }

    [Fact]
    public async Task PreFilter_BijHeaderOnbekendWordenHeaderEnKandidatenNietOpgevraagd()
    {
        var graph = new FakeReplyGraph();
        var mailbox = new RecordingMailbox(graph);
        var email = new Bericht("own", "own@allstars-fc.test");
        var headersRequested = false;

        await EmailBatchFilterKern.PreFilterVoorClassificatieAsync(
            [email], "own@allstars-fc.test", new HashSet<string>(),
            _ => { headersRequested = true; return null; }, graph, mailbox, NullLogger.Instance);

        headersRequested.Should().BeFalse();
        graph.MarkedAsReadIds.Should().ContainSingle("own");
    }

    [Fact]
    public async Task LabelBuitenScope_LabeltEnMarkeertAlleenBuitenScopeBerichten()
    {
        var graph = new FakeReplyGraph();
        var entries = new[]
        {
            (new Bericht("first", "first@allstars-fc.test"), new BerichtClassificatie { Type = VerzoekType.BuitenScope }),
            (new Bericht("in-scope", "normal@allstars-fc.test"), new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek }),
            (new Bericht("second", "second@allstars-fc.test"), new BerichtClassificatie { Type = VerzoekType.BuitenScope })
        };

        await EmailBatchFilterKern.LabelBuitenScopeAsync(entries, email => email.MessageId, graph, NullLogger.Instance);

        graph.CategoryUpdates.Select(update => update.MessageId).Should().Equal("first", "second");
        graph.MarkedAsReadIds.Should().Equal("first", "second");
    }

    [Fact]
    public void FilterUitgeslotenAdressen_GebruiktAfzenderSelectorEnBehoudtInvoerVolgorde()
    {
        var emails = new[]
        {
            new Bericht("one", "blocked@allstars-fc.test"),
            new Bericht("two", "allowed@allstars-fc.test"),
            new Bericht("three", "allowed@allstars-fc.test")
        };

        var result = EmailBatchFilterKern.FilterUitgeslotenAdressen(
            emails, new HashSet<string> { "blocked@allstars-fc.test" }, email => email.Afzender);

        result.Select(email => email.MessageId).Should().Equal("two", "three");
    }

    private sealed record Bericht(
        string MessageId,
        string Afzender,
        IReadOnlyList<KeyValuePair<string, string>>? Headers = null) : IInkomendBericht
    {
        public string AfzenderNaam => "";
        public string Onderwerp => "";
    }

    private sealed class RecordingMailbox(FakeReplyGraph graph) : IEmailMailboxActies
    {
        public Task MarkeerGelezenAsync(string messageId) => graph.MarkAsReadAsync(messageId);

        public async Task LabelGeenAiAntwoordAsync(string messageId)
        {
            await graph.EnsureMasterCategoryAsync(EmailCategorieLabels.GeenAiAntwoord, EmailCategorieLabels.GeenAiAntwoordKleur);
            await graph.SetCategoriesAsync(messageId, EmailCategorieLabels.GeenAiAntwoord);
        }
    }
}
