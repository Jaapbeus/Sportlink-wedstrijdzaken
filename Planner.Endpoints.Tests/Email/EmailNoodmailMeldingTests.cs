using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Endpoints.Email;
using Planner.Shared.Monitoring;
using Xunit;

namespace Planner.Endpoints.Tests.Email;

/// <summary>
/// Noodmails van de e-mailverwerker (#831, #1143), sinds #1716 slice 9 eenmalig in
/// <see cref="EmailNoodmailMelding"/> en daarmee voor beide tiers getest (voorheen een woordelijke kopie
/// per tier). Bewijst dat het throttle-gedrag volledig afhangt van de geïnjecteerde opslag en niet van
/// static procesgeheugen.
/// </summary>
public class EmailNoodmailMeldingTests
{
    private static readonly EmailNoodmailTeksten Teksten = FakePoort.TestTeksten;

    // ── Database-noodmail ────────────────────────────────────────────────────────────────────

    [Fact]
    public void DatabaseNoodmailSleutel_IsGedeeldMetDeUitvalmonitor()
        => EmailNoodmailMelding.DatabaseNoodmailSleutel.Should().Be(DatabaseUitvalCore.NoodmailSleutel);

    [Fact]
    public async Task BehandelDatabaseVerbindingsFout_EersteFout_StuurtNoodmailEnRegistreert()
    {
        var graph = new FakeGraph();
        var store = new FakeThrottle();

        await EmailNoodmailMelding.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db onbereikbaar"), graph, aantalOnverwerkt: 3, store, Teksten, NullLogger.Instance);

        graph.Verstuurd.Should().ContainSingle();
        graph.Verstuurd[0].Subject.Should().Be("URGENT: Database niet bereikbaar — email-processor gepauzeerd");
        graph.Verstuurd[0].Body.Should().Contain("Onverwerkte emails: 3")
            .And.EndWith("beschikbaar is.\n\nTIER-DATABASEADVIES");
        (await store.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel)).Should().NotBeNull();
    }

    [Fact]
    public async Task BehandelDatabaseVerbindingsFout_AlEerderGeregistreerd_StuurtNietOpnieuw()
    {
        var graph = new FakeGraph();
        var store = new FakeThrottle();

        await EmailNoodmailMelding.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db onbereikbaar"), graph, 1, store, Teksten, NullLogger.Instance);
        await EmailNoodmailMelding.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db onbereikbaar"), graph, 2, store, Teksten, NullLogger.Instance);

        graph.Verstuurd.Should().ContainSingle("de tweede aanroep moet onderdrukt blijven");
    }

    [Fact]
    public async Task BehandelDatabaseHerstel_WistRegistratie_ZodatVolgendeUitvalWeerMeldt()
    {
        var graph = new FakeGraph();
        var store = new FakeThrottle();

        await EmailNoodmailMelding.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db onbereikbaar"), graph, 1, store, Teksten, NullLogger.Instance);
        await EmailNoodmailMelding.BehandelDatabaseHerstelAsync(store, NullLogger.Instance);

        (await store.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel)).Should().BeNull();

        await EmailNoodmailMelding.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db opnieuw onbereikbaar"), graph, 1, store, Teksten, NullLogger.Instance);

        graph.Verstuurd.Should().HaveCount(2);
    }

    [Fact]
    public async Task BehandelDatabaseHerstel_ZonderEerdereRegistratie_DoetNiets()
    {
        var store = new FakeThrottle();
        await store.RegistreerVerstuurdAsync(EmailNoodmailMelding.OpenAiQuotaNoodmailSleutel, DateTime.UtcNow);

        await EmailNoodmailMelding.BehandelDatabaseHerstelAsync(store, NullLogger.Instance);

        (await store.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel)).Should().BeNull();
        (await store.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.OpenAiQuotaNoodmailSleutel))
            .Should().NotBeNull("herstel van de database raakt de OpenAI-registratie niet");
    }

    [Fact]
    public async Task StuurDatabaseNoodmail_VerzendingMislukt_RegistreertNietZodatVolgendePollHetOpnieuwProbeert()
    {
        var graph = new FakeGraph { GooiBijVersturen = true };
        var store = new FakeThrottle();

        await EmailNoodmailMelding.StuurDatabaseNoodmailAsync(graph, 1, "test", store, Teksten, NullLogger.Instance);

        (await store.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel))
            .Should().BeNull("een mislukte verzending mag niet als 'verstuurd' geregistreerd worden");
    }

    // ── OpenAI-quota-noodmail (zelfde defectpatroon) ──────────────────────────────────────

    [Fact]
    public async Task MoetOpenAiQuotaNoodmailVersturen_NogNooitVerstuurd_IsTrue()
        => (await EmailNoodmailMelding.MoetOpenAiQuotaNoodmailVersturenAsync(new FakeThrottle(), DateTime.UtcNow))
            .Should().BeTrue();

    [Fact]
    public async Task MoetOpenAiQuotaNoodmailVersturen_BinnenVenster_IsFalse()
    {
        var store = new FakeThrottle();
        var nu = DateTime.UtcNow;
        await store.RegistreerVerstuurdAsync(EmailNoodmailMelding.OpenAiQuotaNoodmailSleutel, nu.AddHours(-1));

        (await EmailNoodmailMelding.MoetOpenAiQuotaNoodmailVersturenAsync(store, nu)).Should().BeFalse();
    }

    [Fact]
    public async Task MoetOpenAiQuotaNoodmailVersturen_PreciesNaVenster_IsTrue()
    {
        var store = new FakeThrottle();
        var nu = DateTime.UtcNow;
        await store.RegistreerVerstuurdAsync(EmailNoodmailMelding.OpenAiQuotaNoodmailSleutel, nu.AddHours(-24));

        (await EmailNoodmailMelding.MoetOpenAiQuotaNoodmailVersturenAsync(store, nu)).Should().BeTrue();
    }

    [Fact]
    public async Task StuurOpenAiNoodmail_Geslaagd_RegistreertMetTierActies()
    {
        var graph = new FakeGraph();
        var store = new FakeThrottle();

        await EmailNoodmailMelding.StuurOpenAiNoodmailAsync(graph, "quota overschreden", store, Teksten, NullLogger.Instance);

        graph.Verstuurd.Should().ContainSingle();
        graph.Verstuurd[0].Subject.Should().Be("URGENT: OpenAI quota overschreden — email-processor gepauzeerd");
        graph.Verstuurd[0].Body.Should().Contain("Foutmelding: quota overschreden")
            .And.Contain("Acties:\n  • TIER-OPENAI-ACTIE\n  • Als de quota verhoogd is");
        (await store.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.OpenAiQuotaNoodmailSleutel)).Should().NotBeNull();
        (await store.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.DatabaseNoodmailSleutel)).Should().BeNull();
    }

    [Fact]
    public async Task StuurOpenAiNoodmail_VerzendingMislukt_RegistreertNiet()
    {
        var graph = new FakeGraph { GooiBijVersturen = true };
        var store = new FakeThrottle();

        await EmailNoodmailMelding.StuurOpenAiNoodmailAsync(graph, "quota", store, Teksten, NullLogger.Instance);

        (await store.LaatsteKeerVerstuurdAsync(EmailNoodmailMelding.OpenAiQuotaNoodmailSleutel)).Should().BeNull();
    }

    // ── #1143: mailboxadres nooit in logs ───────────────────────────────────────────────────

    [Fact]
    public async Task Noodmails_Geslaagd_GaanNaarDeMailboxEnLoggenHetAdresNooit()
    {
        const string mailboxAdres = "noodmail@allstars-fc.test";
        Environment.SetEnvironmentVariable("GraphMailbox", mailboxAdres);
        try
        {
            var graph = new FakeGraph();
            var logger = new OpnameLogger();

            await EmailNoodmailMelding.StuurDatabaseNoodmailAsync(graph, 1, "test", new FakeThrottle(), Teksten, logger);
            await EmailNoodmailMelding.StuurOpenAiNoodmailAsync(graph, "quota", new FakeThrottle(), Teksten, logger);

            graph.Verstuurd.Should().HaveCount(2).And.OnlyContain(r => r.To == mailboxAdres,
                "de test bewijst pas iets als het adres daadwerkelijk gebruikt is om te versturen");
            logger.Berichten.Should().NotBeEmpty().And.NotContain(m => m.Contains(mailboxAdres),
                "SECURITY.md sluit afzender-/ontvangeradressen expliciet uit van logs (#1143)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GraphMailbox", null);
        }
    }
}
