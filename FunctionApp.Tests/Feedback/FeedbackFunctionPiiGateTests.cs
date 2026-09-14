using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Feedback;
using SportlinkFunction.Feedback;
using Xunit;

namespace FunctionApp.Tests.Feedback;

/// <summary>
/// Regressietests voor de PII-gate-hardening (#1006) en de Type-allowlist-gate (#1127).
///
/// De oorspronkelijke #427-gate controleerde alleen <c>dto.Beschrijving</c> + <c>qa.Antwoord</c>, en
/// pas ná de AI-aanroep. Deze tests bewijzen dat de nieuwe gates:
/// - PII in <c>Context.Browser</c>, <c>VragenAntwoorden.Vraag</c> én AI-gegenereerde
///   samenvatting/acceptatiecriteria blokkeren;
/// - vóór elke AI-aanroep draaien (geblokkeerde invoer doet nooit een AI-call);
/// - vlak vóór de GitHub-write nogmaals draaien op de daadwerkelijke titel+body (geblokkeerde
///   AI-output doet nooit een GitHub-call).
///
/// #1127 voegt daar de Type-allowlist-gate aan toe: <c>dto.Type</c> werd vóór #1127 ongefilterd in de
/// AI-prompt geïnterpoleerd zonder dat de PII-gate ernaar keek. Een synthetische PII-marker in Type
/// moet daarom, net als in elk ander veld, tot 0 AI-aanroepen leiden en een afwijzing in zowel
/// Validate als Submit.
/// </summary>
public class FeedbackFunctionPiiGateTests
{
    // Synthetisch testadres — goedgekeurde AVG-veilige placeholder (CLAUDE.md), geen bestaand persoon.
    private const string PiiMarker = "trainer@voorbeeld.nl";

    private static FeedbackRequest MaakSchoonRequest() => new()
    {
        Type = "Fout",
        Beschrijving = "De veldenpagina laadt niet meer na het opslaan van een wijziging.",
        Context = new FeedbackContext
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

    // ── Type-allowlist: blokkeert vóór alle verwerking, ook vóór de PII-gate (#1127) ───────────

    [Fact]
    public async Task ValidateCoreAsync_OngeldigType_WordtGeblokkeerdZonderAiAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Type = "Onbekend";
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackFunction.ValidateCoreAsync(dto, fake, NullLogger.Instance);

        AssertOngeldigType(result);
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task ValidateCoreAsync_PiiInType_WordtGeblokkeerdZonderAiAanroep()
    {
        // Reproductie van bevinding 2 in #1107: vóór de fix accepteerde de server elke string in
        // Type en interpoleerde die ongefilterd in de AI-prompt, zonder dat de PII-gate ernaar keek.
        // De PII-marker is geen toegestane Type-waarde, dus de allowlist-gate blokkeert dit al vóór
        // de AI-aanroep — precies de fix die #1127 vereist.
        var dto = MaakSchoonRequest();
        dto.Type = PiiMarker;
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackFunction.ValidateCoreAsync(dto, fake, NullLogger.Instance);

        AssertOngeldigType(result);
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task SubmitCoreAsync_OngeldigType_WordtGeblokkeerdZonderAiEnGitHubAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Type = "Onbekend";
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackFunction.SubmitCoreAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        AssertOngeldigType(result);
        fake.AantalAanroepen.Should().Be(0);
        github.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task SubmitCoreAsync_PiiInType_WordtGeblokkeerdZonderAiEnGitHubAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Type = PiiMarker;
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackFunction.SubmitCoreAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        AssertOngeldigType(result);
        fake.AantalAanroepen.Should().Be(0);
        github.AantalAanroepen.Should().Be(0);
    }

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
        dto.VragenAntwoorden = [new VraagAntwoord { Vraag = $"Kun je dit mailen naar {PiiMarker}?", Antwoord = "ja" }];
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
        dto.VragenAntwoorden = [new VraagAntwoord { Vraag = $"Mail dit naar {PiiMarker}", Antwoord = "ok" }];
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

    private static void AssertOngeldigType(IActionResult result) =>
        result.Should().BeOfType<BadRequestObjectResult>();

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
