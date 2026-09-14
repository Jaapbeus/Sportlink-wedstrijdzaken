using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Postgres-tier-tegenhanger van
/// <c>FunctionApp.Tests/Email/EmailProcessorFunctionNoodmailTests.cs</c> (#972 — port van
/// EmailProcessorFunction). Woordelijke kopie: bewijst dat het noodmail-throttle-gedrag volledig
/// afhangt van de geïnjecteerde <c>INoodmailThrottleStore</c> en niet van static procesgeheugen.
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

    // ── OpenAI-quota-noodmail (zelfde defectpatroon) ──────────────────────────────────────

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

    // ── #1143: mailboxadres nooit in logs ───────────────────────────────────────────────────

    /// <summary>
    /// Postgres-tier-tegenhanger van de gelijknamige test in
    /// <c>FunctionApp.Tests/Email/EmailProcessorFunctionNoodmailTests.cs</c> (#1143). Bewijst dat
    /// het adres wél gebruikt wordt om te versturen (anders bewijst een lege logregel niets), maar
    /// nergens in een logregel verschijnt.
    /// </summary>
    [Fact]
    public async Task StuurDatabaseNoodmailAsync_Geslaagd_LogtMailboxadresNooit()
    {
        const string mailboxAdres = "noodmail@allstars-fc.test";
        Environment.SetEnvironmentVariable("GraphMailbox", mailboxAdres);
        try
        {
            var graph = new FakeEmailGraphService();
            var store = new FakeNoodmailThrottleStore();
            var logger = new RecordingLogger();

            await EmailProcessorFunction.StuurDatabaseNoodmailAsync(
                graph, aantalEmails: 1, foutmelding: "test", store, logger);

            graph.SentReplies.Should().Contain(r => r.To == mailboxAdres,
                "de test bewijst pas iets als het adres daadwerkelijk gebruikt is om te versturen");
            logger.Messages.Should().NotContain(m => m.Contains(mailboxAdres),
                "SECURITY.md sluit afzender-/ontvangeradressen expliciet uit van logs (#1143)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GraphMailbox", null);
        }
    }

    /// <summary>Zelfde regressie als hierboven, voor de OpenAI-quota-noodmail (#1143).</summary>
    [Fact]
    public async Task StuurOpenAiNoodmailAsync_Geslaagd_LogtMailboxadresNooit()
    {
        const string mailboxAdres = "noodmail@allstars-fc.test";
        Environment.SetEnvironmentVariable("GraphMailbox", mailboxAdres);
        try
        {
            var graph = new FakeEmailGraphService();
            var store = new FakeNoodmailThrottleStore();
            var logger = new RecordingLogger();

            await EmailProcessorFunction.StuurOpenAiNoodmailAsync(graph, "quota overschreden", store, logger);

            graph.SentReplies.Should().Contain(r => r.To == mailboxAdres,
                "de test bewijst pas iets als het adres daadwerkelijk gebruikt is om te versturen");
            logger.Messages.Should().NotContain(m => m.Contains(mailboxAdres),
                "SECURITY.md sluit afzender-/ontvangeradressen expliciet uit van logs (#1143)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GraphMailbox", null);
        }
    }
}
