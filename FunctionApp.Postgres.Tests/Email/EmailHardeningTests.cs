using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using FunctionApp.Postgres.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Postgres-tier-tegenhanger van <c>FunctionApp.Tests/Email/EmailHardeningTests.cs</c> (#972 —
/// port van EmailProcessorFunction). Bevat uitsluitend de scenario's die zonder databaseafhankelijkheid
/// getest kunnen worden (<see cref="UitsluitingslijstCache"/> en
/// <see cref="EmailProcessorFunction.FilterMetVerseUitsluitingslijstAsync"/>).
/// <para>
/// Het SQL Server-origineel test daarnaast <c>EmailPersistenceService.ResolveClubCode</c> (#707) en
/// <c>SqlEmailPersistenceRepository.IsUniekeSleutelFout(int)</c> — beide bestaan op deze tier niet
/// in die vorm: clubCode-resolutie loopt via de al bestaande, elders al geteste
/// <c>PostgresClubScope.Primary</c>, en <c>IsUniekeSleutelFout</c> neemt hier een
/// <c>PostgresException</c> (niet los te construeren buiten een echte unique-violation) — dat pad
/// is al gedekt door <c>PostgresEmailPersistenceIntegrationTests.InsertEmailVerwerking_TweemaalDezelfdeMessageId_LevertDubbeleMessageIdException</c>.
/// De race-conditie-tests op <c>BepaalVerwerkingIdAsync</c>/<c>RegistreerClassificatieFoutAsync</c>
/// staan als integratietests in <see cref="EmailProcessorFunctionIntegrationTests"/>.
/// </para>
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

    [Fact]
    public async Task FilterMetVerseUitsluitingslijst_VerouderdeLijst_LeidtNietMeerTotAiCall()
    {
        var nu = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var cache = new UitsluitingslijstCache();
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

        laadPogingen.Should().Be(1);
        resultaat.Should().NotBeNull();
        resultaat!.Select(e => e.MessageId).Should().Equal(["m2"]);

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
        UitsluitingslijstCache.Ttl.Should().Be(TimeSpan.FromMinutes(15));

        var nu = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var cache = new UitsluitingslijstCache();

        cache.IsVerouderd(nu).Should().BeTrue("een nooit geladen lijst is altijd verouderd");

        await cache.HerlaadAsync(() => Task.FromResult(Lijst()), nu);
        cache.IsVerouderd(nu.AddMinutes(14)).Should().BeFalse();
        cache.IsVerouderd(nu.AddMinutes(15)).Should().BeTrue();
    }
}
