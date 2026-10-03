using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Feedback;
using Xunit;
using static Planner.Shared.Feedback.FeedbackAi;
using static Planner.Shared.Feedback.FeedbackTekst;

namespace Planner.Shared.Tests.Feedback;

/// <summary>Feedbacktekst in het publieke issue mag geen actieve vermeldingen of afbeeldingen bevatten (#1501).</summary>
public class FeedbackVermeldingenTests
{
    private const string Joiner = "‍";

    private static FeedbackRequest Request(string beschrijving, string vraag = "Vraag?", string antwoord = "Antwoord") => new()
    {
        Type = "Fout",
        Beschrijving = beschrijving,
        Context = new FeedbackContext { Pagina = "/velden", Versie = "3.2.2.0", Browser = "TestBrowser/1.0" },
        VragenAntwoorden = [new VraagAntwoord { Vraag = vraag, Antwoord = antwoord }],
    };

    private static string Bouw(FeedbackRequest dto, StructuredIssue? s = null) =>
        FeedbackIssueBody.Bouw(dto, Normaliseer(s ?? new StructuredIssue("t", "s", [])), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Beschrijving_Mention_WordtGeneutraliseerd()
    {
        var body = Bouw(Request("Zie @someuser voor details"));
        body.Should().NotContain("@someuser").And.Contain("@" + Joiner + "someuser");
    }

    [Fact]
    public void Beschrijving_IssueVerwijzing_WordtGeneutraliseerd()
    {
        var body = Bouw(Request("Zelfde als #42"));
        body.Should().NotContain("#42").And.Contain("#" + Joiner + "42");
    }

    [Fact]
    public void Beschrijving_Afbeelding_WordtVerwijderd()
    {
        var body = Bouw(Request("Kijk ![x](https://voorbeeld.invalid/p.png) hier"));
        body.Should().NotContain("![").And.NotContain("voorbeeld.invalid").And.Contain("Kijk  hier");
    }

    [Fact]
    public void VraagEnAntwoord_Worden_Geneutraliseerd()
    {
        var body = Bouw(Request("ok", "Wie is @vraagger?", "Zie #7 ![a](http://x.invalid/i.png) en @antwoorder"));
        body.Should().NotContain("@vraagger").And.NotContain("#7").And.NotContain("@antwoorder").And.NotContain("![");
    }

    [Fact]
    public void SamenvattingEnCriteria_Worden_Geneutraliseerd()
    {
        var body = Bouw(Request("ok"), new StructuredIssue("t", "Meld aan @beheer, zie #9", ["Los #11 op @team", "![i](http://x.invalid/i.png)"]));
        body.Should().NotContain("@beheer").And.NotContain("#9").And.NotContain("#11").And.NotContain("@team").And.NotContain("![");
    }

    [Fact]
    public void Titel_WordtGeneutraliseerd()
    {
        var titel = Sanitize("Fix @user #5", 80);
        titel.Should().NotContain("@user").And.NotContain("#5");
    }

    [Fact]
    public void GewoneTekst_EnEmail_BlijvenOngewijzigd()
    {
        Sanitize("Gewone tekst met punt 3. en een lijst", 200).Should().Be("Gewone tekst met punt 3. en een lijst");
        Sanitize("mail trainer@voorbeeld.nl graag", 200).Should().Be("mail trainer@voorbeeld.nl graag");
    }

    [Fact]
    public void Sanitize_IsIdempotent()
    {
        var eenmaal = Sanitize("@a #1 ![x](y)", 200);
        Sanitize(eenmaal, 200).Should().Be(eenmaal);
    }

    [Fact]
    public async Task AiPrompt_KrijgtGesaneerdePaginaEnVersie()
    {
        var dto = Request("Het werkt niet");
        dto.VragenAntwoorden = null;
        dto.Context = new FeedbackContext { Pagina = "/velden`\n@evil|[x]", Versie = "3.2<b>@x", Browser = "b" };
        var fake = new CapturingChatClient("""{"volledig": true, "vragen": []}""");

        await ValideerVolledigheid(fake, dto, NullLogger.Instance);

        var prompt = fake.Prompt;
        prompt.Should().Contain("Pagina: /velden").And.NotContain("@").And.NotContain("`").And.NotContain("[x]");

        var structuur = new CapturingChatClient("""{"title": "t", "samenvatting": "s", "acceptatiecriteria": []}""");
        await StructureerIssue(structuur, dto, NullLogger.Instance);
        structuur.Prompt.Should().Contain("Pagina: /velden").And.Contain("Versie: 3.2bx").And.NotContain("<b>").And.NotContain("@evil");
    }

    private sealed class CapturingChatClient(string antwoord) : IChatClient
    {
        public string Prompt { get; private set; } = "";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompt = string.Join("\n", messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, antwoord)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
