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

    // ── Voorbeeld vóór publicatie (#1205) ───────────────────────────────────────

    // Vast tijdstip: BouwIssueBody zet een Tijdstip-regel in de body. In productie is dat het
    // publicatiemoment, dus een voorbeeld en de publicatie erna kunnen een minuut schelen. Dat is
    // metadata die de beheerder niet zelf schrijft; om te bewijzen dat de rest van de body
    // tekstueel identiek is, bevriezen deze tests het tijdstip.
    private static readonly DateTime VastTijdstip = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public async Task VoorbeeldAsync_MaaktGeenGitHubIssueAanEnGeeftDezelfdeBodyAlsPublicatie()
    {
        var dto = MaakSchoonRequest();
        var fakeVoorbeeld = new FakeChatClient(GeldigeAiStructuurJson());
        var githubVoorbeeld = new FakeGitHubIssueCreator();

        var voorbeeld = await FeedbackCore.VoorbeeldAsync(dto, fakeVoorbeeld, NullLogger.Instance, VastTijdstip);

        voorbeeld.Status.Should().Be(FeedbackStatus.Ok);
        voorbeeld.Titel.Should().NotBeNullOrWhiteSpace();
        voorbeeld.Body.Should().Contain("Gemeld via feedback widget");
        githubVoorbeeld.AantalAanroepen.Should().Be(0, "een voorbeeld publiceert per definitie niets");

        // Dezelfde invoer + dezelfde AI-uitkomst moet bij publicatie exact dezelfde tekst opleveren,
        // anders is het voorbeeld een leugen.
        var fakeSubmit = new FakeChatClient(GeldigeAiStructuurJson());
        var githubSubmit = new FakeGitHubIssueCreator();
        var submit = await FeedbackCore.SubmitAsync(
            MaakSchoonRequest(), fakeSubmit, githubSubmit.MaakAsync, NullLogger.Instance, VastTijdstip);

        submit.Status.Should().Be(FeedbackStatus.Ok);
        githubSubmit.LaatsteTitel.Should().Be(voorbeeld.Titel);
        githubSubmit.LaatsteBody.Should().Be(voorbeeld.Body);
    }

    [Fact]
    public async Task VoorbeeldAsync_PiiInInvoer_WordtGeweigerdZonderAiAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Beschrijving = $"De pagina laadt niet; mail mij op {PiiMarker} voor details.";
        var fake = new FakeChatClient(GeldigeAiStructuurJson());

        var voorbeeld = await FeedbackCore.VoorbeeldAsync(dto, fake, NullLogger.Instance, VastTijdstip);

        voorbeeld.Status.Should().Be(FeedbackStatus.PiiGedetecteerd);
        voorbeeld.Body.Should().BeNull("een geweigerd voorbeeld geeft nooit de samengestelde tekst terug");
        fake.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task VoorbeeldAsync_PiiInAiOutput_WordtGeweigerdNaAiAanroep()
    {
        var dto = MaakSchoonRequest();
        var fake = new FakeChatClient($$"""
            {"title": "Veldenpagina laadt niet", "samenvatting": "Neem contact op via {{PiiMarker}}.", "acceptatiecriteria": []}
            """);

        var voorbeeld = await FeedbackCore.VoorbeeldAsync(dto, fake, NullLogger.Instance, VastTijdstip);

        voorbeeld.Status.Should().Be(FeedbackStatus.PiiGedetecteerd);
        voorbeeld.Body.Should().BeNull();
        fake.AantalAanroepen.Should().Be(1);
    }

    [Fact]
    public async Task SubmitAsync_MetBevestiging_PubliceertExactDeGetoondeTekstZonderNieuweAiAanroep()
    {
        var dto = MaakSchoonRequest();
        var fakeVoorbeeld = new FakeChatClient(GeldigeAiStructuurJson());
        var voorbeeld = await FeedbackCore.VoorbeeldAsync(dto, fakeVoorbeeld, NullLogger.Instance, VastTijdstip);

        // De bevestigingsstap stuurt terug wat de beheerder gezien heeft. StructureerIssue draait op
        // temperature 0.2 en levert niet tweemaal dezelfde tekst — een tweede AI-aanroep zou het
        // voorbeeld waardeloos maken.
        var bevestigd = MaakSchoonRequest();
        bevestigd.Bevestiging = new FeedbackBevestiging
        {
            Titel = voorbeeld.Titel!,
            Samenvatting = voorbeeld.Samenvatting!,
            Acceptatiecriteria = [.. voorbeeld.Acceptatiecriteria!],
        };

        var fakeSubmit = new FakeChatClient("""{"title": "HEEL ANDERE TITEL", "samenvatting": "anders", "acceptatiecriteria": []}""");
        var github = new FakeGitHubIssueCreator();

        var submit = await FeedbackCore.SubmitAsync(
            bevestigd, fakeSubmit, github.MaakAsync, NullLogger.Instance, VastTijdstip);

        submit.Status.Should().Be(FeedbackStatus.Ok);
        fakeSubmit.AantalAanroepen.Should().Be(0, "de bevestigde velden vervangen de AI-aanroep");
        github.LaatsteTitel.Should().Be(voorbeeld.Titel);
        github.LaatsteBody.Should().Be(voorbeeld.Body);
    }

    [Fact]
    public async Task SubmitAsync_BevestigdeVeldenMetPii_WordtAlsnogGeblokkeerdVoorGitHub()
    {
        // De client mag nooit vertrouwd worden op het punt van publiceren: de PII-gate draait
        // onverkort op de uiteindelijke, samengestelde titel + body.
        var dto = MaakSchoonRequest();
        dto.Bevestiging = new FeedbackBevestiging
        {
            Titel = "Veldenpagina laadt niet",
            Samenvatting = $"Neem contact op via {PiiMarker}.",
            Acceptatiecriteria = [],
        };
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackCore.SubmitAsync(dto, fake, github.MaakAsync, NullLogger.Instance, VastTijdstip);

        result.Status.Should().Be(FeedbackStatus.PiiGedetecteerd);
        fake.AantalAanroepen.Should().Be(0);
        github.AantalAanroepen.Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_BevestigdeVeldenBuitenDeGrenzen_WordenAfgekaptInDeBody()
    {
        // De bevestigde velden komen van de client en waren vóór #1205 altijd AI-output. Zonder
        // normalisatie belandt een megabyte aan samenvatting — of een lijst met honderden criteria —
        // integraal in een openbaar issue, terwijl Beschrijving wél op 2000 tekens wordt afgekapt.
        var dto = MaakSchoonRequest();
        var langeSamenvatting = new string('A', 5000);
        dto.Bevestiging = new FeedbackBevestiging
        {
            Titel = "Veldenpagina laadt niet",
            Samenvatting = langeSamenvatting,
            // Ruim meer dan de AI er ooit produceert (de prompt vraagt om maximaal 5).
            Acceptatiecriteria = [.. Enumerable.Range(1, 50).Select(i => $"Criterium {i} " + new string('B', 300))],
        };
        var fake = new FakeChatClient(GeldigeAiStructuurJson());
        var github = new FakeGitHubIssueCreator();

        var result = await FeedbackCore.SubmitAsync(dto, fake, github.MaakAsync, NullLogger.Instance, VastTijdstip);

        result.Status.Should().Be(FeedbackStatus.Ok);
        var body = github.LaatsteBody!;

        body.Should().NotContain(langeSamenvatting, "de samenvatting hoort afgekapt te zijn");
        body.Should().Contain(new string('A', 500) + "…");

        // Precies vijf criteria, elk afgekapt — niet vijftig.
        body.Split("- [ ] ").Length.Should().Be(6, "vijf criteria leveren vijf scheidingen plus de kop op");
        body.Should().Contain("Criterium 5 ").And.NotContain("Criterium 6 ");
        body.Should().NotContain(new string('B', 300));
    }

    [Fact]
    public async Task VoorbeeldAsync_OngeldigType_WordtGeblokkeerdZonderAiAanroep()
    {
        var dto = MaakSchoonRequest();
        dto.Type = "Onbekend";
        var fake = new FakeChatClient(GeldigeAiStructuurJson());

        var voorbeeld = await FeedbackCore.VoorbeeldAsync(dto, fake, NullLogger.Instance, VastTijdstip);

        voorbeeld.Status.Should().Be(FeedbackStatus.OngeldigType);
        fake.AantalAanroepen.Should().Be(0);
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
        public string? LaatsteTitel { get; private set; }
        public string? LaatsteBody { get; private set; }

        public Task<(int nummer, string url)> MaakAsync(string title, string body, string[] labels)
        {
            AantalAanroepen++;
            LaatsteTitel = title;
            LaatsteBody = body;
            return Task.FromResult((123, "https://github.com/example/repo/issues/123"));
        }
    }
}
