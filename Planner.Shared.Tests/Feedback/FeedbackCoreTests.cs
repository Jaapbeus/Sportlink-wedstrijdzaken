using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Feedback;
using Xunit;

namespace Planner.Shared.Tests.Feedback;

/// <summary>
/// Regressietests voor de provider-onafhankelijke feedbackkern (#129, #1006, #1127), verhuisd uit
/// <c>FunctionApp/Feedback/FeedbackFunction.cs</c>/<c>FunctionApp.Postgres/Feedback/FeedbackFunction.cs</c>
/// naar <see cref="FeedbackCore"/> (#1130). Beide tiers behouden hun eigen
/// <c>FeedbackFunctionPiiGateTests</c> die de dunne <c>ValidateCoreAsync</c>/<c>SubmitCoreAsync</c>-
/// wrappers (en dus ook de vertaling naar <c>IActionResult</c>) testen; deze tests dekken de kern
/// zelf, los van enige tier.
/// </summary>
public class FeedbackCoreTests
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
    public async Task ValidateAsync_OngeldigType_WordtGeblokkeerdZonderAiAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Type = "Onbekend";
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackCore.ValidateAsync(dto, fake, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.OngeldigType);
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task ValidateAsync_PiiInType_WordtGeblokkeerdZonderAiAanroep()
    {
        // Reproductie van bevinding 2 in #1107: vóór de fix accepteerde de server elke string in
        // Type en interpoleerde die ongefilterd in de AI-prompt, zonder dat de PII-gate ernaar keek.
        var dto = MaakSchoonRequest();
        dto.Type = PiiMarker;
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackCore.ValidateAsync(dto, fake, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.OngeldigType);
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_OngeldigType_WordtGeblokkeerdZonderAiEnGitHubAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Type = "Onbekend";
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackCore.SubmitAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.OngeldigType);
        fake.AantalAanroepen.Should().Be(0);
        github.AantalAanroepen.Should().Be(0);
    }

    // ── Validate: blokkeert vóór de AI-aanroep ─────────────────────────────────

    [Fact]
    public async Task ValidateAsync_PiiInContextBrowser_WordtGeblokkeerdZonderAiAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Context!.Browser = $"Mozilla/5.0 (stuur naar {PiiMarker})";
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackCore.ValidateAsync(dto, fake, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.PiiGedetecteerd);
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task ValidateAsync_PiiInVraag_WordtGeblokkeerdZonderAiAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.VragenAntwoorden = [new VraagAntwoord { Vraag = $"Kun je dit mailen naar {PiiMarker}?", Antwoord = "ja" }];
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackCore.ValidateAsync(dto, fake, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.PiiGedetecteerd);
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task ValidateAsync_SchoneInvoer_RoeptAiAanEnGeeftResultaatTerug()
    {
        var dto = MaakSchoonRequest();
        var fake = new FakeChatClient("""{"volledig": true, "vragen": []}""");

        var result = await FeedbackCore.ValidateAsync(dto, fake, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.Ok);
        result.Volledig.Should().BeTrue();
        fake.AantalAanroepen.Should().Be(1);
    }

    // ── Submit: eerste gate blokkeert vóór de AI-aanroep ───────────────────────

    [Fact]
    public async Task SubmitAsync_PiiInContextBrowser_WordtGeblokkeerdZonderAiEnGitHubAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Context!.Browser = $"Mozilla/5.0 (stuur naar {PiiMarker})";
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackCore.SubmitAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.PiiGedetecteerd);
        fake.AantalAanroepen.Should().Be(0);
        github.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_PiiInVraag_WordtGeblokkeerdZonderAiEnGitHubAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.VragenAntwoorden = [new VraagAntwoord { Vraag = $"Mail dit naar {PiiMarker}", Antwoord = "ok" }];
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackCore.SubmitAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.PiiGedetecteerd);
        fake.AantalAanroepen.Should().Be(0);
        github.AantalAanroepen.Should().Be(0);
    }

    // ── Submit: tweede gate blokkeert vlak vóór de GitHub-write, ook bij PII die pas via de
    //    AI-output ontstaat — de eerste gate kan dit per definitie niet zien. ─────────────────

    [Fact]
    public async Task SubmitAsync_PiiInAiSamenvatting_WordtGeblokkeerdVoorGitHubMaarAiIsWelAangeroepen()
    {
        var dto = MaakSchoonRequest();
        var fake = new FakeChatClient($$"""
            {"title": "Veldenpagina laadt niet", "samenvatting": "Neem voor details contact op via {{PiiMarker}}.", "acceptatiecriteria": []}
            """);
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackCore.SubmitAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.PiiGedetecteerd);
        fake.AantalAanroepen.Should().Be(1, "de AI is al aangeroepen — de blokkade zit ná de AI-call, niet ervoor");
        github.AantalAanroepen.Should().Be(0, "een geblokkeerde AI-output mag nooit tot een GitHub-aanroep leiden");
    }

    [Fact]
    public async Task SubmitAsync_PiiInAiAcceptatiecriterium_WordtGeblokkeerdVoorGitHub()
    {
        var dto = MaakSchoonRequest();
        var fake = new FakeChatClient($$"""
            {"title": "Veldenpagina laadt niet", "samenvatting": "Ok.", "acceptatiecriteria": ["Bij fouten mailen naar {{PiiMarker}}"]}
            """);
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackCore.SubmitAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.PiiGedetecteerd);
        github.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_SchoneInvoerEnSchoneAiOutput_MaaktGitHubIssueAan()
    {
        var dto = MaakSchoonRequest();
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackCore.SubmitAsync(dto, fake, github.MaakAsync, NullLogger.Instance);

        result.Status.Should().Be(FeedbackStatus.Ok);
        result.IssueNummer.Should().Be(123);
        fake.AantalAanroepen.Should().Be(1);
        github.AantalAanroepen.Should().Be(1);
    }

    // ── Rate limiter ────────────────────────────────────────────────────────────

    [Fact]
    public void TryAcquireSubmitSlot_TotDeLimiet_GeeftTrueDaarnaFalse()
    {
        // Eigen limiet-venster: dit isoleert de test niet volledig van andere tests die de gedeelde
        // static state gebruiken, maar er is in dit project verder geen andere aanroeper van
        // FeedbackRateLimiter — dezelfde aanname als de oorspronkelijke per-tier tests, die de
        // rate limiter nooit rechtstreeks testten.
        var resultaten = new List<bool>();
        for (var i = 0; i < FeedbackRateLimiter.MaxSubmissiesPerVenster + 2; i++)
            resultaten.Add(FeedbackRateLimiter.TryAcquireSubmitSlot());

        resultaten.Take(FeedbackRateLimiter.MaxSubmissiesPerVenster).Should().OnlyContain(x => x);
        resultaten.Skip(FeedbackRateLimiter.MaxSubmissiesPerVenster).Should().OnlyContain(x => !x);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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
