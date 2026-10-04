using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Feedback;
using Planner.Shared.Feedback;
using Xunit;

namespace Planner.Endpoints.Tests.Feedback;

/// <summary>
/// Regressietests voor de gedeelde feedback-orkestratie (#764, #1476) — het publicatiebeleid, de
/// opslag van de melder, de PII-gates vóór AI en GitHub (de voormalige SubmitCoreAsync-gevallen van
/// beide tiers, #1006/#1127) en de limieten per gebruiker.
/// </summary>
public class FeedbackEndpointCoreTests
{
    // Synthetisch testadres — goedgekeurde AVG-veilige placeholder (CLAUDE.md).
    private const string PiiMarker = "trainer@voorbeeld.nl";
    private const string ClubCode = "ALLSTARS";

    private static readonly FeedbackAanroeper Beheerder = new("00000000-0000-0000-0000-0000000000a1", "Jan de Vries", true);
    private static readonly FeedbackAanroeper Gebruiker = new("00000000-0000-0000-0000-0000000000b2", "Jan de Vries", false);

    private static FeedbackRequest MaakRequest() => new()
    {
        Type = "Fout",
        Beschrijving = "De veldenpagina laadt niet meer na het opslaan van een wijziging.",
        Context = new FeedbackContext { Pagina = "/velden?team=JO13-1", Versie = "3.2.2.0", Browser = "Mozilla/5.0 TestBrowser/1.0" },
    };

    private static string GeldigeAiJson() => """
        {"title": "Veldenpagina laadt niet na opslaan", "samenvatting": "Gebruiker meldt dat de pagina blijft hangen.", "acceptatiecriteria": ["Pagina laadt binnen 2s"]}
        """;

    private sealed class FakeGitHub
    {
        public int Aanroepen { get; private set; }
        public string? Titel { get; private set; }
        public string? Body { get; private set; }
        public string[]? Labels { get; private set; }
        public bool Faalt { get; set; }

        public Task<(int nummer, string url)> MaakAsync(string titel, string body, string[] labels)
        {
            Aanroepen++;
            Titel = titel; Body = body; Labels = labels;
            if (Faalt) throw new InvalidOperationException("GitHub API HTTP 500");
            return Task.FromResult((123, "https://github.com/example/repo/issues/123"));
        }
    }

    private static Task<IActionResult> Submit(
        FeedbackRequest dto, FeedbackAanroeper wie, FeedbackStoreFake store, FakeChat chat, FakeGitHub? github) =>
        FeedbackEndpointCore.SubmitAsync(dto, wie, ClubCode, chat, store,
            github is null ? null : github.MaakAsync, NullLogger.Instance, new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc));

    // ── PII-gates en type-allowlist: vóór AI, vóór opslag, vóór GitHub ─────────────────────────

    [Theory]
    [InlineData("Onbekend")]
    [InlineData(PiiMarker)]
    public async Task OngeldigType_WordtGeblokkeerdZonderAiOpslagEnGitHub(string type)
    {
        var dto = MaakRequest(); dto.Type = type;
        var (store, chat, github) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()), new FakeGitHub());

        var result = await Submit(dto, Beheerder, store, chat, github);

        result.Should().BeOfType<BadRequestObjectResult>();
        (chat.Aanroepen, github.Aanroepen, store.Bewaard.Count).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task PiiInBeschrijving_Geeft422ZonderAiOpslagEnGitHub()
    {
        var dto = MaakRequest(); dto.Beschrijving = $"Mail mij op {PiiMarker} als het niet werkt.";
        var (store, chat, github) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()), new FakeGitHub());

        var result = await Submit(dto, Beheerder, store, chat, github);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        (chat.Aanroepen, github.Aanroepen, store.Bewaard.Count).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task PiiInAiSamenvatting_Geeft422_AiWelAangeroepenMaarNietOpgeslagenOfGepubliceerd()
    {
        var dto = MaakRequest();
        var chat = new FakeChat($$"""{"title": "Veldenpagina", "samenvatting": "Mail {{PiiMarker}}.", "acceptatiecriteria": []}""");
        var (store, github) = (new FeedbackStoreFake(), new FakeGitHub());

        var result = await Submit(dto, Beheerder, store, chat, github);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        chat.Aanroepen.Should().Be(1);
        (github.Aanroepen, store.Bewaard.Count).Should().Be((0, 0));
    }

    [Fact]
    public async Task BevestigdeVeldenMetPii_WordenAlsnogGeblokkeerd()
    {
        var dto = MaakRequest();
        dto.Bevestiging = new FeedbackBevestiging { Titel = "Titel", Samenvatting = $"Mail {PiiMarker}", Acceptatiecriteria = [] };
        var (store, chat, github) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()), new FakeGitHub());

        var result = await Submit(dto, Beheerder, store, chat, github);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        (chat.Aanroepen, github.Aanroepen, store.Bewaard.Count).Should().Be((0, 0, 0));
    }

    // ── Publicatiebeleid (eigenaarsbesluit 2026-10-03) ─────────────────────────────────────────

    [Fact]
    public async Task Beheerder_PubliceertDirectEnBewaartDeMelding()
    {
        var (store, chat, github) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()), new FakeGitHub());

        var result = await Submit(MaakRequest(), Beheerder, store, chat, github);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        github.Aanroepen.Should().Be(1);
        github.Labels.Should().Contain("type: bug");
        var rij = store.Bewaard.Should().ContainSingle().Subject.Rij;
        rij.MelderRol.Should().Be("admin");
        store.Statussen[rij.FeedbackId].Should().Be(FeedbackStatusWaarden.Gepubliceerd);
        store.Gepubliceerd[rij.FeedbackId].Nummer.Should().Be(123);
        ok.Value!.ToString().Should().Contain("123");
    }

    [Fact]
    public async Task Bevestiging_VanGewoneGebruiker_WordtGenegeerdEnDeServerStructureertZelf()
    {
        var dto = MaakRequest();
        dto.Bevestiging = new FeedbackBevestiging { Titel = "Titel van de client", Samenvatting = "Samenvatting van de client", Acceptatiecriteria = [] };
        var (store, chat) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()));

        await Submit(dto, Gebruiker, store, chat, new FakeGitHub());

        chat.Aanroepen.Should().Be(1, "voor de rol user structureert de server, ook als de body een bevestiging bevat");
        var rij = store.Bewaard.Should().ContainSingle().Subject.Rij;
        rij.IssueBody.Should().NotContain("Samenvatting van de client").And.Contain("Gebruiker meldt dat de pagina blijft hangen.");
    }

    [Fact]
    public async Task Bevestiging_VanBeheerder_WordtGebruiktZonderNieuweAiAanroep()
    {
        var dto = MaakRequest();
        dto.Bevestiging = new FeedbackBevestiging { Titel = "Titel van de beheerder", Samenvatting = "Samenvatting van de beheerder", Acceptatiecriteria = [] };
        var (store, chat, github) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()), new FakeGitHub());

        await Submit(dto, Beheerder, store, chat, github);

        chat.Aanroepen.Should().Be(0);
        github.Titel.Should().Be("Titel van de beheerder");
        github.Body.Should().Contain("Samenvatting van de beheerder");
    }

    [Fact]
    public void ControleerEnSaneer_VerwijdertBevestigingVoorGewoneGebruikerMaarNietVoorBeheerder()
    {
        FeedbackRequest MetBevestiging() { var d = MaakRequest(); d.Bevestiging = new FeedbackBevestiging { Titel = "T" }; return d; }
        var voorGebruiker = MetBevestiging();
        var voorBeheerder = MetBevestiging();

        FeedbackEndpointCore.ControleerEnSaneer(voorGebruiker, Gebruiker, "x");
        FeedbackEndpointCore.ControleerEnSaneer(voorBeheerder, Beheerder, "x");

        voorGebruiker.Bevestiging.Should().BeNull();
        voorBeheerder.Bevestiging.Should().NotBeNull();
    }

    [Fact]
    public async Task GewoneGebruiker_WachtOpPublicatie_ZonderGitHubAanroepEnZonderIssueverwijzing()
    {
        var (store, chat, github) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()), new FakeGitHub());

        var result = await Submit(MaakRequest(), Gebruiker, store, chat, github);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        github.Aanroepen.Should().Be(0, "een gebruiker publiceert nooit rechtstreeks; een beheerder klikt eerst");
        var rij = store.Bewaard.Should().ContainSingle().Subject.Rij;
        rij.MelderRol.Should().Be("user");
        rij.Status.Should().Be(FeedbackStatusWaarden.WachtOpPublicatie);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(ok.Value);
        json.Should().NotContain("github.com", "een gewone gebruiker krijgt geen link naar GitHub");
        json.Should().Contain("\"issueNummer\":0");
    }

    [Fact]
    public async Task Melder_WordtOpgeslagen_MaarKomtNooitInHetPubliekeIssue()
    {
        var (store, chat, github) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()), new FakeGitHub());

        await Submit(MaakRequest(), Beheerder, store, chat, github);

        var rij = store.Bewaard.Single().Rij;
        rij.MelderObjectId.Should().Be(Beheerder.ObjectId);
        rij.MelderNaam.Should().Be("Jan de Vries");
        var publiek = github.Titel + "\n" + github.Body;
        publiek.Should().NotContain(Beheerder.ObjectId!).And.NotContain("Jan de Vries");
        rij.IssueBody.Should().NotContain(Beheerder.ObjectId!).And.NotContain("Jan de Vries");
    }

    [Fact]
    public async Task Paginaroute_ZondertQuerystringInPubliekIssueEnOpslag()
    {
        var (store, chat, github) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()), new FakeGitHub());
        var dto = MaakRequest();
        FeedbackEndpointCore.SaneerInvoer(dto, Gebruiker.Naam, Gebruiker.Rol);

        await Submit(dto, Beheerder, store, chat, github);

        store.Bewaard.Single().Rij.Pagina.Should().Be("/velden");
        github.Body.Should().NotContain("JO13-1");
    }

    // ── Technische context ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Telemetrie_WordtGeredigeerdBewaardEnAanDeAiMeegegeven_NietInHetIssue()
    {
        var dto = MaakRequest();
        dto.Telemetrie = new FeedbackTelemetrie
        {
            ConsoleFouten = [$"Fout voor {PiiMarker} bij Jan de Vries"],
            MislukteAanroepen = [new FeedbackApiFout { Methode = "post", Pad = "/api/beheer/velden?id=42", Status = 500 }],
            Navigatiespoor = ["/teams?zoek=JO13-1", "/velden"],
        };
        var (store, chat, github) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()), new FakeGitHub());
        FeedbackEndpointCore.SaneerInvoer(dto, Gebruiker.Naam, Gebruiker.Rol);

        await Submit(dto, Gebruiker, store, chat, github);

        var regels = store.Bewaard.Single().Telemetrie;
        regels.Select(r => r.Bron).Should().BeEquivalentTo("console", "netwerk", "navigatie");
        var alles = string.Join("\n", regels.Select(r => r.Payload));
        alles.Should().NotContain(PiiMarker).And.NotContain("Jan de Vries").And.NotContain("id=42").And.NotContain("zoek=");
        alles.Should().Contain("POST /api/beheer/velden").And.Contain("500");
        chat.LaatsteUserPrompt.Should().Contain("Technische context").And.Contain("500");
        store.Bewaard.Single().Rij.IssueBody.Should().NotContain("Console-fout", "de technische context gaat nooit het (publieke) issue in");
    }

    [Fact]
    public async Task ZonderTelemetrie_WordenGeenTelemetrieRegelsBewaard()
    {
        var (store, chat) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()));

        await Submit(MaakRequest(), Gebruiker, store, chat, new FakeGitHub());

        store.Bewaard.Single().Telemetrie.Should().BeEmpty();
        chat.LaatsteUserPrompt.Should().NotContain("Technische context");
    }

    // ── Limieten ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PerGebruikerLimietBereikt_Geeft429ZonderAiOfOpslag()
    {
        var store = new FeedbackStoreFake { RecentVoorGebruiker = FeedbackEndpointCore.MaxMeldingenPerGebruikerPerVenster };
        var chat = new FakeChat(GeldigeAiJson());

        var result = await Submit(MaakRequest(), Gebruiker, store, chat, new FakeGitHub());

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
        (chat.Aanroepen, store.Bewaard.Count).Should().Be((0, 0));
    }

    [Fact]
    public async Task ClubBrede_Vangnet_Geeft429()
    {
        var store = new FeedbackStoreFake { RecentVoorClub = FeedbackEndpointCore.MaxMeldingenPerClubPerUur };

        var result = await Submit(MaakRequest(), Gebruiker, store, new FakeChat(GeldigeAiJson()), new FakeGitHub());

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
    }

    [Fact]
    public void AiLimiet_IsPerGebruiker_EenAndereGebruikerWordtNietGeblokkeerd()
    {
        var sleutel = "test-" + Guid.NewGuid();
        for (var i = 0; i < FeedbackRateLimiter.MaxAiAanroepenPerVenster; i++)
            FeedbackRateLimiter.TryAcquireAiSlot(sleutel).Should().BeTrue();

        FeedbackRateLimiter.TryAcquireAiSlot(sleutel).Should().BeFalse();
        FeedbackRateLimiter.TryAcquireAiSlot("test-" + Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void ControleerEnSaneer_LegeBeschrijving_Geeft400()
    {
        var dto = MaakRequest(); dto.Beschrijving = " ";

        FeedbackEndpointCore.ControleerEnSaneer(dto, Gebruiker, "verplicht").Should().BeOfType<BadRequestObjectResult>();
        FeedbackEndpointCore.ControleerEnSaneer(null, Gebruiker, "verplicht").Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Foutpaden ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GitHubFaalt_Geeft502_EnMeldingBlijftBewaardAlsGitHubMislukt()
    {
        var (store, chat) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()));

        var result = await Submit(MaakRequest(), Beheerder, store, chat, new FakeGitHub { Faalt = true });

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        store.Statussen[store.Bewaard.Single().Rij.FeedbackId].Should().Be(FeedbackStatusWaarden.GitHubMislukt);
    }

    [Fact]
    public async Task BeheerderZonderGitHubConfiguratie_BewaartDeMeldingEnWaarschuwt()
    {
        var (store, chat) = (new FeedbackStoreFake(), new FakeChat(GeldigeAiJson()));

        var result = await Submit(MaakRequest(), Beheerder, store, chat, github: null);

        result.Should().BeOfType<OkObjectResult>();
        store.Statussen[store.Bewaard.Single().Rij.FeedbackId].Should().Be(FeedbackStatusWaarden.WachtOpPublicatie);
        Newtonsoft.Json.JsonConvert.SerializeObject(((OkObjectResult)result).Value).Should().Contain("niet geconfigureerd");
    }

    [Fact]
    public async Task OpslagFaalt_BijBeheerder_PubliceertDeMeldingToch()
    {
        var store = new FeedbackStoreFake { BewarenFaalt = true };
        var github = new FakeGitHub();

        var result = await Submit(MaakRequest(), Beheerder, store, new FakeChat(GeldigeAiJson()), github);

        result.Should().BeOfType<OkObjectResult>();
        github.Aanroepen.Should().Be(1, "een beheerder verliest zijn melding niet op een weggevallen database");
    }

    [Fact]
    public async Task OpslagFaalt_BijGewoneGebruiker_GeeftEenFout()
    {
        var store = new FeedbackStoreFake { BewarenFaalt = true };

        var act = () => Submit(MaakRequest(), Gebruiker, store, new FakeChat(GeldigeAiJson()), new FakeGitHub());

        await act.Should().ThrowAsync<InvalidOperationException>("een melding die nergens staat heeft de gebruiker niets aan");
    }

    [Fact]
    public void Meldingsnummer_IsAcht_HoofdletterTekens()
    {
        FeedbackEndpointCore.Meldingsnummer(Guid.Parse("3f2a9c1b-0000-0000-0000-000000000000")).Should().Be("3F2A9C1B");
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ControleerAiBeschikbaar_ZonderChatClient_Geeft503MetMelding()
    {
        var uitkomst = FeedbackEndpointCore.ControleerAiBeschikbaar(null, NullLogger.Instance);

        var obj = uitkomst.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(503);
        obj.Value!.ToString().Should().Contain(FeedbackEndpointCore.AiNietBeschikbaarMelding);
    }

    [Fact]
    public void ControleerAiBeschikbaar_MetChatClient_LaatDoor()
    {
        FeedbackEndpointCore.ControleerAiBeschikbaar(new FakeChat("{}"), NullLogger.Instance).Should().BeNull();
    }

    private sealed class FakeChat(string antwoord) : IChatClient
    {
        public int Aanroepen { get; private set; }
        public string LaatsteUserPrompt { get; private set; } = "";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Aanroepen++;
            LaatsteUserPrompt = messages.Last(m => m.Role == ChatRole.User).Text ?? "";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, antwoord)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
