using FluentAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Regressietests voor #831: de noodmail-throttle stond vóór deze fix in een static/volatile veld op
/// <see cref="EmailProcessorFunction"/> — procesgeheugen dat bij elke cold start van de Consumption-
/// plan-worker terugvalt naar de default. Deze tests bewijzen dat het gedrag nu volledig afhangt van de
/// geïnjecteerde <c>INoodmailThrottleStore</c> in plaats van een static veld: elke test bouwt zijn eigen
/// verse store en verse aanroepen op, zonder enige gedeelde static state tussen tests — precies het
/// scenario dat vóór de fix ontbrak.
/// </summary>
public class EmailProcessorFunctionNoodmailTests
{
    // ── Database-noodmail ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BehandelDatabaseVerbindingsFoutAsync_EersteFout_StuurtNoodmailEnRegistreert()
    {
        var graph = new FakeEmailGraphService();
        var store = new FakeNoodmailThrottleStore();

        await EmailProcessorFunction.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db onbereikbaar"), graph, aantalOnverwerkt: 3, store, NullLogger.Instance);

        graph.SentReplies.Should().ContainSingle();
        (await store.LaatsteKeerVerstuurdAsync(EmailProcessorFunction.DatabaseNoodmailSleutel))
            .Should().NotBeNull();
    }

    /// <summary>
    /// De kern van #831: een tweede, onafhankelijke aanroep — te vergelijken met een nieuwe invocatie ná
    /// een cold start — gebruikt dezelfde (persistente) store-instantie en moet daarom nog steeds
    /// onderdrukt worden. Vóór de fix stond deze registratie in een static veld dat bij een echte cold
    /// start gereset zou zijn; hier bewijst hergebruik van dezelfde `store`-instantie dat het gedrag nu
    /// van die store afhangt, niet van procesgeheugen.
    /// </summary>
    [Fact]
    public async Task BehandelDatabaseVerbindingsFoutAsync_AlEerderGeregistreerd_StuurtNietOpnieuw()
    {
        var graph = new FakeEmailGraphService();
        var store = new FakeNoodmailThrottleStore();

        await EmailProcessorFunction.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db onbereikbaar"), graph, aantalOnverwerkt: 1, store, NullLogger.Instance);
        await EmailProcessorFunction.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db onbereikbaar"), graph, aantalOnverwerkt: 2, store, NullLogger.Instance);

        graph.SentReplies.Should().ContainSingle("de tweede aanroep moet onderdrukt blijven");
    }

    [Fact]
    public async Task BehandelDatabaseHerstelAsync_WisRegistratie_ZodatVolgendeUitvalWeerMeldt()
    {
        var graph = new FakeEmailGraphService();
        var store = new FakeNoodmailThrottleStore();

        await EmailProcessorFunction.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db onbereikbaar"), graph, aantalOnverwerkt: 1, store, NullLogger.Instance);
        await EmailProcessorFunction.BehandelDatabaseHerstelAsync(store, NullLogger.Instance);

        (await store.LaatsteKeerVerstuurdAsync(EmailProcessorFunction.DatabaseNoodmailSleutel))
            .Should().BeNull();

        // Een nieuwe uitval na herstel moet weer een verse melding opleveren.
        await EmailProcessorFunction.BehandelDatabaseVerbindingsFoutAsync(
            new InvalidOperationException("db opnieuw onbereikbaar"), graph, aantalOnverwerkt: 1, store, NullLogger.Instance);

        graph.SentReplies.Should().HaveCount(2);
    }

    [Fact]
    public async Task BehandelDatabaseHerstelAsync_ZonderEerdereRegistratie_DoetNiets()
    {
        var store = new FakeNoodmailThrottleStore();

        await EmailProcessorFunction.BehandelDatabaseHerstelAsync(store, NullLogger.Instance);

        (await store.LaatsteKeerVerstuurdAsync(EmailProcessorFunction.DatabaseNoodmailSleutel))
            .Should().BeNull();
    }

    [Fact]
    public async Task StuurDatabaseNoodmailAsync_VerzendfoutMislukt_RegistreertNietZodatVolgendePollHetOpnieuwProbeert()
    {
        var graph = new FakeEmailGraphService { ThrowOnSendReply = true };
        var store = new FakeNoodmailThrottleStore();

        await EmailProcessorFunction.StuurDatabaseNoodmailAsync(
            graph, aantalEmails: 1, foutmelding: "test", store, NullLogger.Instance);

        (await store.LaatsteKeerVerstuurdAsync(EmailProcessorFunction.DatabaseNoodmailSleutel))
            .Should().BeNull("een mislukte verzending mag niet als 'verstuurd' geregistreerd worden");
    }

    // ── OpenAI-quota-noodmail (zelfde defectpatroon, #831) ──────────────────────────────────────

    [Fact]
    public async Task MoetOpenAiQuotaNoodmailVersturenAsync_NogNooitVerstuurd_GeeftTrueTerug()
    {
        var store = new FakeNoodmailThrottleStore();

        var moetVersturen = await EmailProcessorFunction.MoetOpenAiQuotaNoodmailVersturenAsync(store, DateTime.UtcNow);

        moetVersturen.Should().BeTrue();
    }

    [Fact]
    public async Task MoetOpenAiQuotaNoodmailVersturenAsync_BinnenVenster_GeeftFalseTerug()
    {
        var store = new FakeNoodmailThrottleStore();
        var nu = DateTime.UtcNow;
        await store.RegistreerVerstuurdAsync(EmailProcessorFunction.OpenAiQuotaNoodmailSleutel, nu.AddHours(-1));

        var moetVersturen = await EmailProcessorFunction.MoetOpenAiQuotaNoodmailVersturenAsync(store, nu);

        moetVersturen.Should().BeFalse();
    }

    [Fact]
    public async Task MoetOpenAiQuotaNoodmailVersturenAsync_NaVenster_GeeftTrueTerug()
    {
        var store = new FakeNoodmailThrottleStore();
        var nu = DateTime.UtcNow;
        await store.RegistreerVerstuurdAsync(EmailProcessorFunction.OpenAiQuotaNoodmailSleutel, nu.AddHours(-25));

        var moetVersturen = await EmailProcessorFunction.MoetOpenAiQuotaNoodmailVersturenAsync(store, nu);

        moetVersturen.Should().BeTrue();
    }

    [Fact]
    public async Task StuurOpenAiNoodmailAsync_Geslaagd_RegistreertInDeStore()
    {
        var graph = new FakeEmailGraphService();
        var store = new FakeNoodmailThrottleStore();

        await EmailProcessorFunction.StuurOpenAiNoodmailAsync(graph, "quota overschreden", store, NullLogger.Instance);

        graph.SentReplies.Should().ContainSingle();
        (await store.LaatsteKeerVerstuurdAsync(EmailProcessorFunction.OpenAiQuotaNoodmailSleutel))
            .Should().NotBeNull();
    }
}
