using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Email;
using Planner.Shared.Email;
using Xunit;

namespace Planner.Endpoints.Tests.Email;

/// <summary>
/// Fase-orkestratie van de e-mailverwerker (#1629, sinds #1716 slice 9 tier-onafhankelijk in
/// <see cref="EmailVerwerkerFasen"/>): volgorde, vroege stops, noodmails en foutafhandeling per bericht.
/// De tier-kant is een programmeerbare <see cref="FakePoort"/>; per tier bewijst een eigen omhulseltest
/// dat de echte poort aangesloten is.
/// </summary>
public class EmailVerwerkerFasenTests
{
    private static readonly DateTime Nu = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private static HashSet<string> Lijst(params string[] adressen) => new(adressen, StringComparer.OrdinalIgnoreCase);

    private static Task VoerUitAsync(FakePoort poort, UitsluitingslijstCache? cache = null)
        => EmailVerwerkerFasen.VoerUitAsync(poort, cache ?? new UitsluitingslijstCache(), NullLogger.Instance);

    private static EmailClassificatieBatch<TestBericht> Batch(
        IEnumerable<(TestBericht, VerzoekType)> items, bool afgebroken = false, Exception? quota = null)
        => new(items.Select(i => (i.Item1, new BerichtClassificatie { Type = i.Item2 })).ToList(), afgebroken, quota);

    // ── Pure kern van fase 1 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BepaalMislukteClassificaties_BerichtZonderClassificatie_Telt()
    {
        TestBericht a = new("a"), b = new("b");

        var mislukt = EmailVerwerkerFasen.BepaalMislukteClassificaties([a, b], [(a, new BerichtClassificatie())], aiAborted: false);

        mislukt.Should().ContainSingle().Which.MessageId.Should().Be("b");
    }

    [Fact]
    public void BepaalMislukteClassificaties_AfgebrokenBatch_TeltNiemandMee()
    {
        TestBericht a = new("a"), b = new("b");

        EmailVerwerkerFasen.BepaalMislukteClassificaties([a, b], [(a, new BerichtClassificatie())], aiAborted: true)
            .Should().BeEmpty();
    }

    [Fact]
    public void BepaalMislukteClassificaties_AllesGeclassificeerd_IsLeeg()
    {
        TestBericht a = new("a");

        EmailVerwerkerFasen.BepaalMislukteClassificaties([a], [(a, new BerichtClassificatie())], aiAborted: false)
            .Should().BeEmpty();
    }

    // ── Uitsluitingslijst vóór de AI-stap (#709, #423) ───────────────────────────────────────

    private static List<TestBericht> FilterOpAfzender(List<TestBericht> e, IReadOnlySet<string> l)
        => e.Where(b => !l.Contains(b.Afzender)).ToList();

    [Fact]
    public async Task FilterMetVerseUitsluitingslijst_VerouderdeLijst_FiltertMetDeVerseLijst()
    {
        var cache = new UitsluitingslijstCache();
        await cache.HerlaadAsync(() => Task.FromResult(Lijst()), Nu - UitsluitingslijstCache.Ttl);
        var laadPogingen = 0;

        var resultaat = await EmailVerwerkerFasen.FilterMetVerseUitsluitingslijstAsync(
            [new TestBericht("m1", "netuitgesloten@voorbeeld.nl"), new TestBericht("m2")],
            cache,
            () => { laadPogingen++; return Task.FromResult(Lijst("netuitgesloten@voorbeeld.nl")); },
            FilterOpAfzender, Nu, NullLogger.Instance);

        laadPogingen.Should().Be(1);
        resultaat!.Select(e => e.MessageId).Should().Equal(["m2"]);
    }

    [Fact]
    public async Task FilterMetVerseUitsluitingslijst_BinnenGeldigheidsduur_RaaktDeDatabaseNiet()
    {
        var cache = new UitsluitingslijstCache();
        await cache.HerlaadAsync(() => Task.FromResult(Lijst("uitgesloten@voorbeeld.nl")), Nu);
        var laadPogingen = 0;
        var batch = new List<TestBericht> { new("m1") };

        var resultaat = await EmailVerwerkerFasen.FilterMetVerseUitsluitingslijstAsync(
            batch, cache, () => { laadPogingen++; return Task.FromResult(Lijst()); },
            (_, _) => throw new InvalidOperationException("geen filter zonder verse lijst"),
            Nu.AddMinutes(1), NullLogger.Instance);

        laadPogingen.Should().Be(0);
        resultaat.Should().BeSameAs(batch);
    }

    [Fact]
    public async Task FilterMetVerseUitsluitingslijst_ColdStartZonderDatabase_GeeftNull()
    {
        var cache = new UitsluitingslijstCache();

        var resultaat = await EmailVerwerkerFasen.FilterMetVerseUitsluitingslijstAsync(
            [new TestBericht("m1")], cache,
            () => throw new InvalidOperationException("database niet bereikbaar (gesimuleerd)"),
            FilterOpAfzender, Nu, NullLogger.Instance);

        resultaat.Should().BeNull();
        cache.IsGeladen.Should().BeFalse();
    }

    [Fact]
    public async Task FilterMetVerseUitsluitingslijst_VerversenMisluktNaEerderSucces_HoudtEerdereLijst()
    {
        var cache = new UitsluitingslijstCache();
        await cache.HerlaadAsync(() => Task.FromResult(Lijst("uitgesloten@voorbeeld.nl")), Nu - UitsluitingslijstCache.Ttl);
        var batch = new List<TestBericht> { new("m1") };

        var resultaat = await EmailVerwerkerFasen.FilterMetVerseUitsluitingslijstAsync(
            batch, cache, () => throw new InvalidOperationException("database niet bereikbaar (gesimuleerd)"),
            FilterOpAfzender, Nu, NullLogger.Instance);

        resultaat.Should().BeSameAs(batch);
        cache.Adressen.Should().Contain("uitgesloten@voorbeeld.nl");
    }

    // ── Fase 1: vroege stops, de database blijft slapen ──────────────────────────────────────

    [Fact]
    public async Task LegeInbox_StoptNaHetOphalen()
    {
        var poort = new FakePoort();

        await VoerUitAsync(poort);

        poort.Stappen.Should().Equal("haal");
    }

    [Fact]
    public async Task AllesWeggefilterdDoorVoorfilter_GeenAiEnGeenDatabase()
    {
        var poort = new FakePoort { Ongelezen = [new("m1")], PreFilter = _ => [] };

        await VoerUitAsync(poort);

        poort.Stappen.Should().Equal("haal", "prefilter");
    }

    [Fact]
    public async Task VerseUitsluitingslijstFiltertAlles_GeenAi()
    {
        var poort = new FakePoort
        {
            Ongelezen = [new("m1", "netuitgesloten@voorbeeld.nl")],
            UitsluitingslijstVoorAi = Lijst("netuitgesloten@voorbeeld.nl")
        };

        await VoerUitAsync(poort);

        poort.Stappen.Should().Equal("haal", "prefilter", "lijst-voor-ai", "filter");
    }

    [Fact]
    public async Task UitsluitingslijstNooitTeLaden_FailClosed_GeenAi()
    {
        var poort = new FakePoort { Ongelezen = [new("m1")], LijstVoorAiFout = new InvalidOperationException("database niet bereikbaar") };

        await VoerUitAsync(poort);

        poort.Stappen.Should().Equal("haal", "prefilter", "lijst-voor-ai");
    }

    [Fact]
    public async Task AiNietBeschikbaar_StoptVoorClassificatie()
    {
        var poort = new FakePoort { Ongelezen = [new("m1")], AiBeschikbaar = false };

        await VoerUitAsync(poort);

        poort.Stappen.Should().Equal("haal", "prefilter", "lijst-voor-ai", "filter", "ai");
    }

    [Fact]
    public async Task AllesBuitenScope_AlleenLabelen_DatabaseBlijftSlapen()
    {
        TestBericht m1 = new("m1");
        var poort = new FakePoort { Ongelezen = [m1], Classificeer = _ => Batch([(m1, VerzoekType.BuitenScope)]) };

        await VoerUitAsync(poort);

        poort.Stappen.Should().Equal("haal", "prefilter", "lijst-voor-ai", "filter", "ai", "classificeer", "label");
    }

    // ── OpenAI-quota ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QuotaAfgebroken_StuurtEenmaligNoodmail_EnTeltRestNietAlsMislukt()
    {
        TestBericht m1 = new("m1"), m2 = new("m2");
        var poort = new FakePoort
        {
            Ongelezen = [m1, m2],
            Classificeer = _ => Batch([], afgebroken: true, quota: new InvalidOperationException("429 insufficient_quota"))
        };

        await VoerUitAsync(poort);
        await VoerUitAsync(poort);

        poort.FakeGraph.Verstuurd.Should().ContainSingle()
            .Which.Subject.Should().StartWith("URGENT: OpenAI quota overschreden");
        poort.Stappen.Should().NotContain("wacht", "zonder te verwerken berichten blijft de database slapen");
    }

    [Fact]
    public async Task AfgebrokenZonderQuotaFout_GeenNoodmail()
    {
        TestBericht m1 = new("m1");
        var poort = new FakePoort { Ongelezen = [m1], Classificeer = _ => Batch([], afgebroken: true) };

        await VoerUitAsync(poort);

        poort.FakeGraph.Verstuurd.Should().BeEmpty();
    }

    // ── Fase 2 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VolledigePoll_VolgordeVanWekkenTotVerwerken()
    {
        TestBericht m1 = new("m1"), m2 = new("m2"), m3 = new("m3");
        var poort = new FakePoort
        {
            Ongelezen = [m1, m2, m3],
            // m2 faalt bij de AI-classificatie: in fase 2 alsnog geregistreerd (pogingenteller).
            Classificeer = _ => Batch([(m1, VerzoekType.HerplanVerzoek), (m3, VerzoekType.BuitenScope)])
        };

        await VoerUitAsync(poort);

        poort.Stappen.Should().Equal(
            "haal", "prefilter", "lijst-voor-ai", "filter", "ai", "classificeer", "label",
            "wacht", "instellingen", "lijst", "voorbereiding", "classificatiefout:m2", "verwerk:m1");
        poort.Verwerkt.Select(v => v.MessageId).Should().Equal(["m1"], "buiten scope gaat niet naar de verwerking");
    }

    [Fact]
    public async Task Verwerking_KrijgtDeInDezePollHerladenUitsluitingslijst()
    {
        TestBericht m1 = new("m1");
        var poort = new FakePoort { Ongelezen = [m1], UitsluitingslijstUitDatabase = Lijst("vers@voorbeeld.nl") };

        await VoerUitAsync(poort);

        poort.Verwerkt.Should().ContainSingle().Which.Uitgesloten.Should().Contain("vers@voorbeeld.nl");
    }

    [Fact]
    public async Task DatabaseOnbereikbaar_StuurtNoodmailMetAantalEnVerwerktNiets()
    {
        TestBericht m1 = new("m1"), m2 = new("m2");
        var poort = new FakePoort
        {
            Ongelezen = [m1, m2],
            Classificeer = _ => Batch([(m1, VerzoekType.HerplanVerzoek)]),
            DatabaseFout = new TimeoutException("db slaapt")
        };

        await VoerUitAsync(poort);
        await VoerUitAsync(poort);

        var mail = poort.FakeGraph.Verstuurd.Should().ContainSingle("de tweede uitval-poll blijft stil").Which;
        mail.Subject.Should().StartWith("URGENT: Database niet bereikbaar");
        mail.Body.Should().Contain("Onverwerkte emails: 2").And.EndWith(FakePoort.TestTeksten.DatabaseAdvies);
        poort.Stappen.Should().NotContain(s => s == "instellingen" || s.StartsWith("verwerk:") || s.StartsWith("classificatiefout:"));
    }

    [Fact]
    public async Task DatabaseWeerBereikbaar_WistDeNoodmailRegistratie()
    {
        var poort = new FakePoort { Ongelezen = [new("m1")] };
        await poort.FakeThrottle.RegistreerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel, Nu);

        await VoerUitAsync(poort);

        (await poort.FakeThrottle.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel)).Should().BeNull();
        poort.Verwerkt.Should().ContainSingle();
    }

    [Fact]
    public async Task VoorbereidingMislukt_GeenRegistratieEnGeenVerwerking()
    {
        TestBericht m1 = new("m1"), m2 = new("m2");
        var poort = new FakePoort
        {
            Ongelezen = [m1, m2],
            Classificeer = _ => Batch([(m1, VerzoekType.HerplanVerzoek)]),
            VerwerkingGereed = false
        };

        await VoerUitAsync(poort);

        poort.Stappen.Should().EndWith("voorbereiding");
    }

    [Fact]
    public async Task FoutBijEenBericht_LegtGesaneerdVastEnVerwerktDeRest()
    {
        TestBericht m1 = new("m1"), m2 = new("m2");
        var poort = new FakePoort
        {
            Ongelezen = [m1, m2],
            VerwerkFout = b => b.MessageId == "m1" ? new InvalidOperationException("kapot voor trainer@voorbeeld.nl") : null
        };

        await VoerUitAsync(poort);

        poort.VastgelegdeFouten.Should().ContainSingle()
            .Which.Should().Be(("m1", "kapot voor [e-mail]"));
        poort.Verwerkt.Select(v => v.MessageId).Should().Equal(["m2"]);
    }
}
