using FluentAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Regressietests voor de hardening van de e-mailverwerking:
/// een lege ClubCode die als "ontbrekend" moet gelden (#707), de race op MessageId tussen twee
/// invocaties (#707), en het verversen van de uitsluitingslijst vóór de AI-stap (#709).
/// </summary>
public class EmailHardeningTests
{
    private static InkomendBericht Bericht(string messageId, string afzender = "trainer@voorbeeld.test") => new()
    {
        MessageId = messageId,
        Afzender = afzender,
        Onderwerp = "Test",
        Body = "Test",
        OntvangstDatum = DateTime.UtcNow
    };

    private static HashSet<string> Lijst(params string[] adressen)
        => new(adressen, StringComparer.OrdinalIgnoreCase);

    // ── #707: lege ClubCode is ontbrekende ClubCode ───────────────────────────────────────────

    /// <summary>
    /// <c>?? throw</c> ving alleen <c>null</c>. LoadSettingsAsync zet een lege kolomwaarde echter als
    /// <c>""</c> in de settings-cache, dus kwam die lege waarde ongehinderd door.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void ResolveClubCode_LegeInstelling_GooitInvalidOperation(string clubCode)
    {
        var service = new EmailPersistenceService(new FakeEmailPersistenceRepository(), () => clubCode);

        var act = () => service.ResolveClubCode();

        act.Should().Throw<InvalidOperationException>().WithMessage("*clubCode*");
    }

    /// <summary>
    /// Het faalscenario zelf: met ClubCode <c>""</c> gaf de query voor de uitsluitingslijst een lege
    /// set terug, waarna uitgesloten adressen alsnog werden verwerkt en beantwoord — fail-open op een
    /// AVG-maatregel. De lijst mag daarom nooit "leeg maar geldig" opleveren; de query hoort niet
    /// eens te draaien.
    /// </summary>
    [Fact]
    public async Task LaadUitgeslotenAdressenAsync_LegeClubCode_VoertGeenQueryUitEnFaalt()
    {
        var repo = new FakeEmailPersistenceRepository();
        repo.ExcludedAddressesToReturn.Add("uitgesloten@voorbeeld.test");
        var service = new EmailPersistenceService(repo, () => "");

        var act = async () => await service.LaadUitgeslotenAdressenAsync(NullLogger.Instance);

        await act.Should().ThrowAsync<InvalidOperationException>();
        repo.LastExcludedClubCode.Should().BeNull();
    }

    // ── #707: check-then-act op MessageId ────────────────────────────────────────────────────

    [Theory]
    [InlineData(2627)] // unique constraint
    [InlineData(2601)] // unique index
    public void IsUniekeSleutelFout_HerkentBeideSqlFoutnummers(int foutnummer)
        => EmailProcessingRepository.IsUniekeSleutelFout(foutnummer).Should().BeTrue();

    [Theory]
    [InlineData(547)]   // foreign key / check constraint
    [InlineData(1205)]  // deadlock
    [InlineData(2)]     // netwerk/timeout
    [InlineData(0)]
    public void IsUniekeSleutelFout_AndereFoutenBlijvenGewoneFouten(int foutnummer)
        => EmailProcessingRepository.IsUniekeSleutelFout(foutnummer).Should().BeFalse();

    /// <summary>
    /// Faalscenario: twee overlappende invocaties zien beide "geen rij", waarna de tweede INSERT op
    /// UQ_EmailVerwerking_MessageId klapt. De foutafhandeling zoekt op MessageId (niet op Id) en zette
    /// daarmee de rij van de eerste invocatie op 'Fout' — terwijl die het antwoord juist wél
    /// verstuurde. Deze poging moet dus stoppen zónder iets weg te schrijven.
    /// </summary>
    [Fact]
    public async Task BepaalVerwerkingIdAsync_GelijktijdigeRegistratie_StoptZonderFoutstatus()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService
        {
            StandToReturn = null,
            ThrowDubbeleMessageIdOnInsert = true
        };

        var verwerkingId = await EmailProcessorFunction.BepaalVerwerkingIdAsync(
            Bericht("m-race"), graph, persistence, NullLogger.Instance);

        verwerkingId.Should().BeNull();
        persistence.FoutUpdates.Should().BeEmpty();
        persistence.StatusUpdates.Should().BeEmpty();
        persistence.PogingVerhogingen.Should().BeEmpty();

        // De andere invocatie is eigenaar van dit bericht: niets versturen, niets als gelezen zetten.
        graph.SentReplies.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().BeEmpty();
    }

    [Fact]
    public async Task BepaalVerwerkingIdAsync_NieuwBericht_GeeftIdVanDeNieuweRij()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService { StandToReturn = null };

        var verwerkingId = await EmailProcessorFunction.BepaalVerwerkingIdAsync(
            Bericht("m-nieuw"), graph, persistence, NullLogger.Instance);

        verwerkingId.Should().Be(1);
        persistence.Inserts.Should().ContainSingle(e => e.MessageId == "m-nieuw");
        persistence.FoutUpdates.Should().BeEmpty();
    }

    [Fact]
    public async Task RegistreerClassificatieFoutAsync_GelijktijdigeRegistratie_LegtGeenFoutVast()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService
        {
            StandToReturn = null,
            ThrowDubbeleMessageIdOnInsert = true
        };

        await EmailProcessorFunction.RegistreerClassificatieFoutAsync(
            Bericht("m-race-classificatie"), graph, persistence, NullLogger.Instance);

        persistence.FoutUpdates.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().BeEmpty();
    }

    // ── #709: uitsluitingslijst vers vóór de AI-stap ─────────────────────────────────────────

    /// <summary>
    /// Faalscenario: de beheerder sluit een adres uit. Fase 1 werkte met de in-memory kopie die pas in
    /// fase 2 werd ververst, en fase 2 werd niet bereikt zolang elk bericht buiten scope viel. Het
    /// adres kreeg dus terecht geen antwoord, maar de body was al naar de externe AI-provider gestuurd.
    /// </summary>
    [Fact]
    public async Task FilterMetVerseUitsluitingslijst_VerouderdeLijst_LeidtNietMeerTotAiCall()
    {
        var nu = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var cache = new UitsluitingslijstCache();
        // Kopie van vóór de uitsluiting: het adres stond er nog niet in.
        await cache.HerlaadAsync(() => Task.FromResult(Lijst()), nu - UitsluitingslijstCache.Ttl);

        var laadPogingen = 0;
        var batch = new List<InkomendBericht>
        {
            Bericht("m1", "netuitgesloten@voorbeeld.test"),
            Bericht("m2")
        };

        var resultaat = await EmailProcessorFunction.FilterMetVerseUitsluitingslijstAsync(
            batch,
            cache,
            () => { laadPogingen++; return Task.FromResult(Lijst("netuitgesloten@voorbeeld.test")); },
            new EmailBatchFilterService(),
            nu,
            NullLogger.Instance);

        // Ververst vóórdat er geclassificeerd wordt, en het uitgesloten bericht is eruit gefilterd.
        laadPogingen.Should().Be(1);
        resultaat.Should().NotBeNull();
        resultaat!.Select(e => e.MessageId).Should().Equal(["m2"]);

        // Bewijs dat de AI-provider het uitgesloten adres niet meer ziet.
        var aangebodenAanAi = new List<string>();
        await new EmailClassificationService().ClassificeerBatchAsync(
            resultaat!,
            email =>
            {
                aangebodenAanAi.Add(email.Afzender);
                return Task.FromResult(new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek });
            },
            _ => false,
            NullLogger.Instance);

        aangebodenAanAi.Should().Equal(["trainer@voorbeeld.test"]);
    }

    [Fact]
    public async Task FilterMetVerseUitsluitingslijst_BinnenGeldigheidsduur_RaaktDeDatabaseNiet()
    {
        var nu = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var cache = new UitsluitingslijstCache();
        await cache.HerlaadAsync(() => Task.FromResult(Lijst("uitgesloten@voorbeeld.test")), nu);

        var laadPogingen = 0;
        var batch = new List<InkomendBericht> { Bericht("m1") };

        var resultaat = await EmailProcessorFunction.FilterMetVerseUitsluitingslijstAsync(
            batch,
            cache,
            () => { laadPogingen++; return Task.FromResult(Lijst()); },
            new EmailBatchFilterService(),
            nu.AddMinutes(1),
            NullLogger.Instance);

        laadPogingen.Should().Be(0);
        resultaat.Should().BeSameAs(batch);
    }

    /// <summary>
    /// Fail-closed bij een koude start blijft gelden (#423): zonder ooit geladen uitsluitingslijst mag
    /// er geen enkel bericht naar de AI-provider.
    /// </summary>
    [Fact]
    public async Task FilterMetVerseUitsluitingslijst_ColdStartZonderDatabase_GeeftNull()
    {
        var cache = new UitsluitingslijstCache();

        var resultaat = await EmailProcessorFunction.FilterMetVerseUitsluitingslijstAsync(
            [Bericht("m1")],
            cache,
            () => throw new InvalidOperationException("database niet bereikbaar (gesimuleerd)"),
            new EmailBatchFilterService(),
            new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc),
            NullLogger.Instance);

        resultaat.Should().BeNull();
        cache.IsGeladen.Should().BeFalse();
    }

    /// <summary>
    /// Mislukt het verversen terwijl er al een lijst is, dan blijft die lijst gelden en gaat de
    /// verwerking door — precies het gedrag van vóór de geldigheidsduur. Stilzetten zou van een
    /// tijdelijk niet-bereikbare database een volledige verwerkingsstop maken.
    /// </summary>
    [Fact]
    public async Task FilterMetVerseUitsluitingslijst_VerversenMisluktNaEerderSucces_HoudtEerdereLijst()
    {
        var nu = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var cache = new UitsluitingslijstCache();
        await cache.HerlaadAsync(() => Task.FromResult(Lijst("uitgesloten@voorbeeld.test")), nu - UitsluitingslijstCache.Ttl);

        var batch = new List<InkomendBericht> { Bericht("m1") };

        var resultaat = await EmailProcessorFunction.FilterMetVerseUitsluitingslijstAsync(
            batch,
            cache,
            () => throw new InvalidOperationException("database niet bereikbaar (gesimuleerd)"),
            new EmailBatchFilterService(),
            nu,
            NullLogger.Instance);

        resultaat.Should().BeSameAs(batch);
        cache.Adressen.Should().Contain("uitgesloten@voorbeeld.test");
    }

    [Fact]
    public async Task UitsluitingslijstCache_VerlooptNaVijftienMinuten()
    {
        // Ruimer dan het poll-interval van 5 minuten: bij elke poll herladen zou de Azure SQL
        // Serverless-database wakker houden voor batches die anders niet in de database komen.
        UitsluitingslijstCache.Ttl.Should().Be(TimeSpan.FromMinutes(15));

        var nu = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var cache = new UitsluitingslijstCache();

        cache.IsVerouderd(nu).Should().BeTrue("een nooit geladen lijst is altijd verouderd");

        await cache.HerlaadAsync(() => Task.FromResult(Lijst()), nu);
        cache.IsVerouderd(nu.AddMinutes(14)).Should().BeFalse();
        cache.IsVerouderd(nu.AddMinutes(15)).Should().BeTrue();
    }
}
