using FluentAssertions;
using FunctionApp.Postgres.Feedback;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FunctionApp.Postgres.Tests.Feedback;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp.Tests/Feedback/FeedbackFunctionPiiGateTests.cs</c>
/// (#1006) — dekt dezelfde PII-gate-hardening voor de vrijwel woordelijke kopie in
/// <c>FunctionApp.Postgres/Feedback/FeedbackFunction.cs</c>.
///
/// De oorspronkelijke #427-gate controleerde alleen <c>dto.Beschrijving</c> + <c>qa.Antwoord</c>, en
/// pas ná de AI-aanroep. Deze tests bewijzen dat de nieuwe gates:
/// - PII in <c>Context.Browser</c>, <c>VragenAntwoorden.Vraag</c> én AI-gegenereerde
///   samenvatting/acceptatiecriteria blokkeren;
/// - vóór elke AI-aanroep draaien (geblokkeerde invoer doet nooit een AI-call);
/// - vlak vóór de GitHub-write nogmaals draaien op de daadwerkelijke titel+body (geblokkeerde
///   AI-output doet nooit een GitHub-call).
/// </summary>
public class FeedbackFunctionPiiGateTests
{
    // Synthetisch testadres — goedgekeurde AVG-veilige placeholder (CLAUDE.md), geen bestaand persoon.
    private const string PiiMarker = "trainer@voorbeeld.nl";

    private static FeedbackFunction.FeedbackRequest MaakSchoonRequest() => new()
    {
        Type = "Fout",
        Beschrijving = "De veldenpagina laadt niet meer na het opslaan van een wijziging.",
        Context = new FeedbackFunction.FeedbackContext
        {
            Pagina = "/velden",
            Versie = "3.2.2.0",
            Browser = "Mozilla/5.0 TestBrowser/1.0",
        },
        VragenAntwoorden = null,
    };

    private static string GeldigeAiStructuurJson() => """
        {"title": "Veldenpagina laadt niet na opslaan", "samenvatting": "Gebruiker meldt dat de pagina blijft hangen na het opslaan van een wijziging.", "acceptatiecriteria": ["Pagina laadt binnen 2s na opslaan"]}
        """;

    // ── Validate: blokkeert vóór de AI-aanroep ─────────────────────────────────

    [Fact]
    public async Task ValidateCoreAsync_PiiInContextBrowser_WordtGeblokkeerdZonderAiAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Context!.Browser = $"Mozilla/5.0 (stuur naar {PiiMarker})";
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackFunction.ValidateCoreAsync(dto, fake, NullLogger.Instance);

        AssertGeblokkeerd(result);
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task ValidateCoreAsync_PiiInVraag_WordtGeblokkeerdZonderAiAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.VragenAntwoorden = [new FeedbackFunction.VraagAntwoord { Vraag = $"Kun je dit mailen naar {PiiMarker}?", Antwoord = "ja" }];
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackFunction.ValidateCoreAsync(dto, fake, NullLogger.Instance);

        AssertGeblokkeerd(result);
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task ValidateCoreAsync_SchoneInvoer_RoeptAiAanEnGeeftResultaatTerug()
    {
        var dto = MaakSchoonRequest();
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackFunction.ValidateCoreAsync(dto, fake, NullLogger.Instance);

        result.Should().BeOfType<OkObjectResult>();
        fake.AantalAanroepen.Should().Be(1);
    }

    // ── Submit: eerste gate blokkeert vóór de AI-aanroep ───────────────────────

    [Fact]
    public async Task SubmitCoreAsync_PiiInContextBrowser_WordtGeblokkeerdZonderAiEnGitHubAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Context!.Browser = $"Mozilla/5.0 (stuur naar {PiiMarker})";
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackFunction.SubmitCoreAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        AssertGeblokkeerd(result);
        fake.AantalAanroepen.Should().Be(0);
        github.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task SubmitCoreAsync_PiiInVraag_WordtGeblokkeerdZonderAiEnGitHubAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.VragenAntwoorden = [new FeedbackFunction.VraagAntwoord { Vraag = $"Mail dit naar {PiiMarker}", Antwoord = "ok" }];
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackFunction.SubmitCoreAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        AssertGeblokkeerd(result);
        fake.AantalAanroepen.Should().Be(0);
        github.AantalAanroepen.Should().Be(0);
    }

    // ── Submit: tweede gate blokkeert vlak vóór de GitHub-write, ook bij PII die pas via de
    //    AI-output ontstaat — de eerste gate kan dit per definitie niet zien. ─────────────────

    [Fact]
    public async Task SubmitCoreAsync_PiiInAiSamenvatting_WordtGeblokkeerdVoorGitHubMaarAiIsWelAangeroepen()
    {
        // Schone invoer — de eerste gate laat dit door. Het taalmodel genereert (hier gesimuleerd
        // via een fake) een samenvatting die de PII-marker bevat. De tweede gate, vlak vóór de
        // GitHub-write, moet dit alsnog blokkeren.
        var dto = MaakSchoonRequest();
        var fake = new FakeChatClient($$"""
            {"title": "Veldenpagina laadt niet", "samenvatting": "Neem voor details contact op via {{PiiMarker}}.", "acceptatiecriteria": []}
            """);
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackFunction.SubmitCoreAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        AssertGeblokkeerd(result);
        fake.AantalAanroepen.Should().Be(1, "de AI is al aangeroepen — de blokkade zit ná de AI-call, niet ervoor");
        github.AantalAanroepen.Should().Be(0, "een geblokkeerde AI-output mag nooit tot een GitHub-aanroep leiden");
    }

    [Fact]
    public async Task SubmitCoreAsync_PiiInAiAcceptatiecriterium_WordtGeblokkeerdVoorGitHub()
    {
        var dto = MaakSchoonRequest();
        var fake = new FakeChatClient($$"""
            {"title": "Veldenpagina laadt niet", "samenvatting": "Ok.", "acceptatiecriteria": ["Bij fouten mailen naar {{PiiMarker}}"]}
            """);
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackFunction.SubmitCoreAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        AssertGeblokkeerd(result);
        github.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task SubmitCoreAsync_SchoneInvoerEnSchoneAiOutput_MaaktGitHubIssueAan()
    {
        var dto = MaakSchoonRequest();
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackFunction.SubmitCoreAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        result.Should().BeOfType<OkObjectResult>();
        fake.AantalAanroepen.Should().Be(1);
        github.AantalAanroepen.Should().Be(1);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void AssertGeblokkeerd(IActionResult result)
    {
        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
    }

    private sealed class FakeChatClient(string antwoord) : IChatClient
    {
        public int AantalAanroepen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            AantalAanroepen++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, antwoord)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FakeGitHubIssueCreator
    {
        public int AantalAanroepen { get; private set; }

        public Task<(int nummer, string url)> MaakAsync(string title, string body, string[] labels)
        {
            AantalAanroepen++;
            return Task.FromResult((123, "https://github.com/example/repo/issues/123"));
        }
    }
}
