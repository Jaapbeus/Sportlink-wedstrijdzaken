using AwesomeAssertions;
using Microsoft.Extensions.Logging;
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
    public async Task PreFilter_SlaatHeadersOverVoorEigenMailboxEnUitgeslotenAfzender()
    {
        var graph = new FakeReplyGraph();
        var mailbox = new RecordingMailbox(graph);
        var emails = new[]
        {
            new Bericht("own", "OWN@allstars-fc.test"),
            new Bericht("blocked", "BLOCKED@allstars-fc.test")
        };

        var result = await EmailBatchFilterKern.PreFilterVoorClassificatieAsync(
            emails, "own@allstars-fc.test",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked@allstars-fc.test" },
            _ => throw new InvalidOperationException("Headers should not be read for filtered senders"),
            graph, mailbox, NullLogger.Instance);

        result.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().Equal("own", "blocked");
    }

    [Fact]
    public async Task PreFilter_BehoudtComparerVanUitsluitingsset()
    {
        var graph = new FakeReplyGraph();
        var mailbox = new RecordingMailbox(graph);
        var email = new Bericht("blocked", "BLOCKED@allstars-fc.test");

        var result = await EmailBatchFilterKern.PreFilterVoorClassificatieAsync(
            [email], "own@allstars-fc.test",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked@allstars-fc.test" },
            bericht => bericht.Headers, graph, mailbox, NullLogger.Instance);

        result.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().ContainSingle("blocked");
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

    [Fact]
    public void FilterUitgeslotenAdressen_BehoudtComparerVanUitsluitingsset()
    {
        var email = new Bericht("blocked", "BLOCKED@allstars-fc.test");

        var result = EmailBatchFilterKern.FilterUitgeslotenAdressen(
            [email], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked@allstars-fc.test" },
            bericht => bericht.Afzender);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LabelBuitenScope_BijCategorieFoutWaarschuwtEnGaatDoorMetVolgendBericht()
    {
        var graph = new FailingCategoryGraph("first");
        var logger = new WarningLogger();
        var entries = new[]
        {
            (new Bericht("first", "one@allstars-fc.test"), new BerichtClassificatie { Type = VerzoekType.BuitenScope }),
            (new Bericht("second", "two@allstars-fc.test"), new BerichtClassificatie { Type = VerzoekType.BuitenScope })
        };

        await EmailBatchFilterKern.LabelBuitenScopeAsync(entries, email => email.MessageId, graph, logger);

        logger.Warnings.Should().ContainSingle(message => message.Contains("Fout bij Outlook-labeling BuitenScope", StringComparison.Ordinal));
        graph.MarkedAsReadIds.Should().Equal("second");
        graph.CategoryUpdates.Should().ContainSingle().Which.Should().Be("second");
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

    private sealed class FailingCategoryGraph(string failingMessageId) : IReplyGraph
    {
        public List<string> CategoryUpdates { get; } = [];
        public List<string> MarkedAsReadIds { get; } = [];

        public Task SetCategoriesAsync(string messageId, params string[] categories)
        {
            if (messageId == failingMessageId)
                throw new ArgumentException("synthetic category failure");
            CategoryUpdates.Add(messageId);
            return Task.CompletedTask;
        }

        public Task EnsureMasterCategoryAsync(string name, string colorPreset) => Task.CompletedTask;
        public Task MarkAsReadAsync(string messageId)
        {
            MarkedAsReadIds.Add(messageId);
            return Task.CompletedTask;
        }

        public Task SendReplyAsync(string to, string subject, string body, string? conversationId,
            IReadOnlyList<string>? bcc = null, EmailBijlage? bijlage = null) => Task.CompletedTask;
    }

    private sealed class WarningLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
