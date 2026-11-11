using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Feedback;
using Planner.Shared.Feedback;
using Xunit;

namespace Planner.Endpoints.Tests.Feedback;

/// <summary>Beheeroverzicht (#764, #1478): filters, inzagelog en de publicatieknop voor wachtende meldingen.</summary>
public class FeedbackBeheerEndpointCoreTests
{
    private const string ClubCode = "ALLSTARS";
    private static readonly FeedbackAanroeper Beheerder = new("00000000-0000-0000-0000-0000000000a1", "Testbeheerder", true);
    private static readonly Guid Id = Guid.Parse("3f2a9c1b-1111-2222-3333-444455556666");

    private static FeedbackDetail MaakDetail(string status = FeedbackStatusWaarden.WachtOpPublicatie, string body = "Schone issuetekst.") => new(
        new FeedbackSamenvatting(Id, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), "Fout", "Veldenpagina laadt niet",
            "Jan de Vries", false, "user", "/velden", null, null, status),
        "Eigen woorden van de melder", null, body, "3.9.7.0", []);

    private static Func<string, string, string[], Task<(int, string)>> GitHub(out List<string> aanroepen, bool faalt = false)
    {
        var lijst = new List<string>();
        aanroepen = lijst;
        return (titel, body, labels) =>
        {
            lijst.Add(titel);
            if (faalt) throw new InvalidOperationException("HTTP 500");
            return Task.FromResult((77, "https://github.com/example/repo/issues/77"));
        };
    }

    // ── Lijst ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lijst_LogtDeInzage_ZonderDeZoektekst()
    {
        var store = new FeedbackStoreFake();

        var result = await FeedbackBeheerEndpointCore.LijstAsync(ClubCode, Beheerder, store,
            "Fout", "wacht-op-publicatie", "2026-09-01", null, "jan de vries", "25", "0");

        result.Should().BeOfType<OkObjectResult>();
        var log = store.Inzage.Should().ContainSingle().Subject;
        log.Actie.Should().Be("lijst");
        log.InzienDoorObjectId.Should().Be(Beheerder.ObjectId);
        log.InzienDoorNaam.Should().Be("Testbeheerder");
        log.Filter.Should().Contain("type=Fout").And.Contain("zoekterm=ja").And.NotContain("jan de vries");
        store.LaatsteFilter!.Limit.Should().Be(25);
    }

    [Theory]
    [InlineData("Onbekend", null, null)]
    [InlineData(null, "bestaat-niet", null)]
    [InlineData(null, null, "morgen")]
    public async Task Lijst_OngeldigFilter_Geeft400ZonderInzagelog(string? type, string? status, string? vanaf)
    {
        var store = new FeedbackStoreFake();

        var result = await FeedbackBeheerEndpointCore.LijstAsync(ClubCode, Beheerder, store, type, status, vanaf, null, null, null, null);

        result.Should().BeOfType<BadRequestObjectResult>();
        store.Inzage.Should().BeEmpty();
    }

    [Fact]
    public async Task Lijst_BegrenstLimitEnTotIsInclusief()
    {
        var store = new FeedbackStoreFake();

        await FeedbackBeheerEndpointCore.LijstAsync(ClubCode, Beheerder, store, null, null, null, "2026-10-03", null, "99999", "-5");

        store.LaatsteFilter!.Limit.Should().Be(FeedbackEndpointCore.MaxLimit);
        store.LaatsteFilter.Offset.Should().Be(0);
        store.LaatsteFilter.TotUtc.Should().Be(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc), "tot en met 3 oktober");
    }

    // ── Detail ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_LogtDeInzageMetHetMeldingId()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail() };

        var result = await FeedbackBeheerEndpointCore.DetailAsync(ClubCode, Beheerder, store, Id);

        result.Should().BeOfType<OkObjectResult>();
        var log = store.Inzage.Should().ContainSingle().Subject;
        (log.Actie, log.FeedbackId).Should().Be(("detail", (Guid?)Id));
    }

    [Fact]
    public async Task Detail_OnbekendeMelding_Geeft404ZonderInzagelog()
    {
        var store = new FeedbackStoreFake();

        var result = await FeedbackBeheerEndpointCore.DetailAsync(ClubCode, Beheerder, store, Id);

        result.Should().BeOfType<NotFoundObjectResult>();
        store.Inzage.Should().BeEmpty();
    }

    // ── Publiceren ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publiceer_WachtendeMelding_MaaktIssueEnLogtDeActie()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail() };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<OkObjectResult>();
        aanroepen.Should().ContainSingle();
        store.Gepubliceerd[Id].Nummer.Should().Be(77);
        store.Inzage.Should().ContainSingle(i => i.Actie == "publiceer");
    }

    [Fact]
    public async Task Publiceer_AlGepubliceerd_Geeft409ZonderGitHubAanroep()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail(FeedbackStatusWaarden.Gepubliceerd) };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<ConflictObjectResult>();
        aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task Publiceer_ClaimMislukt_Geeft409OmEenDubbelIssueTeVoorkomen()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail(), ClaimSlaagt = false };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<ConflictObjectResult>();
        aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task Publiceer_TekstMetPii_WordtAlsnogGeblokkeerd()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail(body: "Mail trainer@voorbeeld.nl voor details.") };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task Publiceer_GitHubFaalt_Geeft502EnZetStatusOpMislukt()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail() };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out _, faalt: true), NullLogger.Instance);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        store.Statussen[Id].Should().Be(FeedbackStatusWaarden.GitHubMislukt);
    }

    [Fact]
    public async Task Publiceer_GitHubUitkomstOnbekend_Geeft502EnBehoudtClaim()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail() };
        var githubCalls = 0;
        Func<string, string, string[], Task<(int, string)>> github = (_, _, _) =>
        {
            githubCalls++;
            throw new FeedbackPublicatieUitkomstOnbekendException(new TaskCanceledException());
        };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, github, NullLogger.Instance);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        githubCalls.Should().Be(1);
        store.Statussen[Id].Should().Be(FeedbackStatusWaarden.Publiceren,
            "an ambiguous POST stays claimed; recovery goes through the reference check, not a blind retry");
    }

    // ── Automatisch herstel van een vastgelopen publicatie (#1803, besluit 2C) ─────────────────

    private const string Referentie = "abcdefghijklmnopqrstuvwx";
    private static FeedbackDetail MaakVastgelopen() =>
        MaakDetail(FeedbackStatusWaarden.Publiceren, FeedbackPublicatieReferentie.VoegToe("Schone issuetekst.", Referentie));

    private static Func<string, DateTime, Task<(bool, FeedbackGevondenIssue?)>> Zoek(
        out List<(string Referentie, DateTime Sinds)> aanroepen, FeedbackGevondenIssue? gevonden = null, bool gelukt = true, bool gooit = false)
    {
        var lijst = new List<(string, DateTime)>();
        aanroepen = lijst;
        return (r, sinds) =>
        {
            lijst.Add((r, sinds));
            if (gooit) throw new HttpRequestException("onbereikbaar");
            return Task.FromResult((gelukt, gevonden));
        };
    }

    [Fact]
    public async Task Herstel_IssueBestaatAl_KoptHetZonderNieuwIssue()
    {
        var store = new FeedbackStoreFake { Detail = MaakVastgelopen() };
        store.Statussen[Id] = FeedbackStatusWaarden.Publiceren;
        var zoek = Zoek(out var zoeken, new FeedbackGevondenIssue(41, "https://github.com/example/repo/issues/41"));

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance, zoek);

        result.Should().BeOfType<OkObjectResult>();
        aanroepen.Should().BeEmpty("het bestaande issue wordt gekoppeld, er komt geen tweede");
        store.Gepubliceerd[Id].Nummer.Should().Be(41);
        zoeken.Should().ContainSingle().Which.Referentie.Should().Be(Referentie);
        zoeken[0].Sinds.Should().Be(store.HerClaimVorigMoment!.Value - FeedbackPublicatieHerstel.ZoekMarge);
    }

    [Fact]
    public async Task Herstel_IssueBestaatNiet_PlaatstOpnieuwMetDezelfdeReferentie()
    {
        var store = new FeedbackStoreFake { Detail = MaakVastgelopen() };
        store.Statussen[Id] = FeedbackStatusWaarden.Publiceren;
        var bodies = new List<string>();
        Func<string, string, string[], Task<(int, string)>> github = (_, body, _) =>
        {
            bodies.Add(body);
            return Task.FromResult((77, "https://github.com/example/repo/issues/77"));
        };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, github, NullLogger.Instance, Zoek(out _));

        result.Should().BeOfType<OkObjectResult>();
        store.Gepubliceerd[Id].Nummer.Should().Be(77);
        FeedbackPublicatieReferentie.Lees(bodies.Single()).Should().Be(Referentie, "de referentie wordt bij elke poging hergebruikt");
    }

    [Fact]
    public async Task Herstel_GitHubOnbereikbaar_BlijftHerstelbaarZonderNieuwIssue()
    {
        foreach (var variant in new[] { Zoek(out _, gelukt: false), Zoek(out _, gooit: true) })
        {
            var store = new FeedbackStoreFake { Detail = MaakVastgelopen() };
            store.Statussen[Id] = FeedbackStatusWaarden.Publiceren;

            var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance, variant);

            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
            aanroepen.Should().BeEmpty("zonder zekerheid over een bestaand issue wordt niet geplaatst");
            store.Statussen[Id].Should().Be(FeedbackStatusWaarden.Publiceren, "de claim blijft herstelbaar");
        }
    }

    [Fact]
    public async Task Herstel_ClaimNogVers_Geeft409ZonderZoekenOfPlaatsen()
    {
        var store = new FeedbackStoreFake { Detail = MaakVastgelopen(), HerClaimVorigMoment = null };
        store.Statussen[Id] = FeedbackStatusWaarden.Publiceren;

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance, Zoek(out var zoeken));

        result.Should().BeOfType<ConflictObjectResult>();
        aanroepen.Should().BeEmpty();
        zoeken.Should().BeEmpty();
    }

    [Fact]
    public async Task Herstel_NieuwePlaatsingOnbekendeUitkomst_BlijftOpnieuwHerstelbaar()
    {
        var store = new FeedbackStoreFake { Detail = MaakVastgelopen() };
        store.Statussen[Id] = FeedbackStatusWaarden.Publiceren;
        Func<string, string, string[], Task<(int, string)>> github = (_, _, _) =>
            throw new FeedbackPublicatieUitkomstOnbekendException(new TaskCanceledException());

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, github, NullLogger.Instance, Zoek(out _));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        store.Statussen[Id].Should().Be(FeedbackStatusWaarden.Publiceren);
    }

    [Fact]
    public async Task Herstel_MisluktePogingen_SchuivenDeZoekgrensNietOp_EnKoppelenZonderTweedeIssue()
    {
        // Review-P2: elke herclaim ververste vroeger het claimmoment, waarna de volgende poging vanaf dát moment zocht en
        // het issue van de eerste POST miste. Nu: eerste POST dubbelzinnig, twee zoekfouten, één gevonden-maar-receipt-faalt,
        // en pas de vijfde poging koppelt — alle zoekacties starten bij het begin van de eerste poging.
        var store = new FeedbackStoreFake { Detail = MaakDetail(body: FeedbackPublicatieReferentie.VoegToe("Schone issuetekst.", Referentie)) };
        var posts = 0;
        Func<string, string, string[], Task<(int, string)>> github = (_, _, _) =>
        {
            posts++;
            throw new FeedbackPublicatieUitkomstOnbekendException(new TaskCanceledException());
        };
        var bestaand = new FeedbackGevondenIssue(41, "https://github.com/example/repo/issues/41");
        var antwoorden = new Queue<Func<(bool, FeedbackGevondenIssue?)>>(new Func<(bool, FeedbackGevondenIssue?)>[]
        {
            () => (false, null), () => throw new HttpRequestException("onbereikbaar"), () => (true, bestaand), () => (true, bestaand),
        });
        var zoeken = new List<DateTime>();
        Func<string, DateTime, Task<(bool, FeedbackGevondenIssue?)>> zoek = (_, sinds) =>
        {
            zoeken.Add(sinds);
            return Task.FromResult(antwoorden.Dequeue()());
        };
        var stap = TimeSpan.FromMinutes(6);

        (await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, github, NullLogger.Instance, zoek))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        var eerstePost = store.PublicatieGestart[Id];
        store.ReceiptFaaltKeren = 1;
        var uitkomsten = new List<IActionResult>();
        for (var poging = 1; poging <= 4; poging++)
        {
            store.VerstrijkTijd(stap);
            uitkomsten.Add(await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, github, NullLogger.Instance, zoek));
        }

        uitkomsten.Take(3).Should().AllSatisfy(r => r.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502));
        uitkomsten[3].Should().BeOfType<OkObjectResult>();
        posts.Should().Be(1, "na de eerste dubbelzinnige POST wordt nooit blind opnieuw geplaatst");
        store.Gepubliceerd[Id].Nummer.Should().Be(41);
        zoeken.Should().HaveCount(4);
        for (var i = 0; i < 4; i++)
            zoeken[i].Should().Be(eerstePost - (stap * (i + 1)) - FeedbackPublicatieHerstel.ZoekMarge,
                $"poging {i + 1} zoekt vanaf het begin van de eerste POST, ook na mislukte herstelpogingen");
    }

    [Fact]
    public async Task Publiceer_OudereMeldingZonderReferentie_KrijgtVoorDeEerstePostEenBewaardeReferentie()
    {
        // Review-P3: een melding van vóór #1803 krijgt onder de claim, vóór de eerste POST, alsnog een referentie.
        var store = new FeedbackStoreFake { Detail = MaakDetail() };
        var bodies = new List<string>();
        Func<string, string, string[], Task<(int, string)>> github = (_, body, _) =>
        {
            bodies.Add(body);
            throw new FeedbackPublicatieUitkomstOnbekendException(new TaskCanceledException());
        };

        await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, github, NullLogger.Instance, Zoek(out _));

        var bewaard = store.IssueBodyWijzigingen.Should().ContainSingle().Subject.Body;
        var referentie = FeedbackPublicatieReferentie.Lees(bewaard);
        referentie.Should().NotBeNull();
        bodies.Single().Should().Be(bewaard, "precies de bewaarde tekst gaat naar GitHub");

        // Het herstel daarna zoekt op die bewaarde referentie en voegt geen nieuwe toe.
        store.VerstrijkTijd(TimeSpan.FromMinutes(6));
        await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, github, NullLogger.Instance, Zoek(out var zoeken));
        zoeken.Should().ContainSingle().Which.Referentie.Should().Be(referentie);
        store.IssueBodyWijzigingen.Should().ContainSingle();
    }

    [Fact]
    public async Task Publiceer_ReferentieNietBewaard_PostNietEnGeeftDeClaimVrij()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail(), IssueBodyBewarenFaalt = true };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        aanroepen.Should().BeEmpty("zonder bewaarde referentie zou een dubbelzinnige POST niet herstelbaar zijn");
        store.Statussen[Id].Should().Be(FeedbackStatusWaarden.GitHubMislukt, "er is niets verstuurd, dus de melding is gewoon opnieuw te publiceren");
    }

    [Fact]
    public async Task Herstel_ZonderZoekdelegate_Geeft503ZonderClaimWijziging()
    {
        var store = new FeedbackStoreFake { Detail = MaakVastgelopen() };
        store.Statussen[Id] = FeedbackStatusWaarden.Publiceren;

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, GitHub(out var aanroepen), NullLogger.Instance);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
        aanroepen.Should().BeEmpty();
    }

    [Fact]
    public async Task Publiceer_ZonderGitHubConfiguratie_Geeft503()
    {
        var store = new FeedbackStoreFake { Detail = MaakDetail() };

        var result = await FeedbackBeheerEndpointCore.PubliceerAsync(ClubCode, Beheerder, store, Id, null, NullLogger.Instance);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
    }
}
