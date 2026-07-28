using FluentAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Regressietests voor de foutafhandeling in de e-mailprocessor (#712):
/// buiten-scope ná herclassificatie, en het vastleggen van mislukte AI-classificaties.
/// </summary>
public class EmailProcessorFunctionTests
{
    private static InkomendBericht Bericht(string messageId = "m1") => new()
    {
        MessageId = messageId,
        Afzender = "afzender@voorbeeld.test",
        Onderwerp = "Test",
        Body = "Test",
        OntvangstDatum = DateTime.UtcNow
    };

    // ── Buiten scope ná herclassificatie ─────────────────────────────────────────────────────

    /// <summary>
    /// Faalscenario: de herclassificatie met leermomenten levert 'buiten scope' op. Dat kwam niet
    /// meer langs het voorfilter van fase 1, waardoor er tóch een automatisch antwoord uitging —
    /// terwijl exact dezelfde e-mail zonder leermomenten in de database géén antwoord kreeg.
    /// </summary>
    [Fact]
    public async Task HandelBuitenScopeAsync_BuitenScope_ZetStatus_LabeltEnVerstuurtNiets()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();

        var afgehandeld = await EmailProcessorFunction.HandelBuitenScopeAsync(
            verwerkingId: 11,
            messageId: "m1",
            classificatie: new BerichtClassificatie { Type = VerzoekType.BuitenScope },
            classificatieJson: "{\"Type\":4}",
            graphService: graph,
            persistenceService: persistence,
            log: NullLogger.Instance);

        afgehandeld.Should().BeTrue();
        persistence.StatusUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 11 && u.Status == EmailStatus.BuitenScope && u.GeextraheerdeData == "{\"Type\":4}");

        // Zelfde eindresultaat als het voorfilter van fase 1: label, gelezen, geen antwoord.
        graph.CategoryUpdates.Should().ContainSingle(c => c.Categories.Contains("Geen AI antwoord"));
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1");
        graph.SentReplies.Should().BeEmpty();
    }

    [Theory]
    [InlineData(VerzoekType.BeschikbaarheidCheck)]
    [InlineData(VerzoekType.HerplanVerzoek)]
    [InlineData(VerzoekType.TeamContactOpvragen)]
    public async Task HandelBuitenScopeAsync_BinnenScope_LaatVerwerkingDoorlopen(VerzoekType type)
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();

        var afgehandeld = await EmailProcessorFunction.HandelBuitenScopeAsync(
            verwerkingId: 12,
            messageId: "m2",
            classificatie: new BerichtClassificatie { Type = type },
            classificatieJson: "{}",
            graphService: graph,
            persistenceService: persistence,
            log: NullLogger.Instance);

        afgehandeld.Should().BeFalse();
        persistence.StatusUpdates.Should().BeEmpty();
        graph.CategoryUpdates.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().BeEmpty();
    }

    // ── Mislukte AI-classificatie ────────────────────────────────────────────────────────────

    /// <summary>
    /// Faalscenario: de AI-classificatie faalt. Er kwam geen rij in de database, dus was er geen
    /// spoor en geen teller — het bericht kwam elke poll terug en kostte elke keer een AI-call.
    /// </summary>
    [Fact]
    public async Task RegistreerClassificatieFoutAsync_EersteFout_LegtRijVastEnLaatBerichtOngelezen()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService { StandToReturn = null };

        await EmailProcessorFunction.RegistreerClassificatieFoutAsync(
            Bericht(), graph, persistence, NullLogger.Instance);

        persistence.Inserts.Should().ContainSingle(e => e.MessageId == "m1");
        // Sinds #717 muteert de foutafhandeling op verwerkingId; de fake geeft 1 terug bij de insert.
        persistence.FoutUpdates.Should().ContainSingle(u => u.VerwerkingId == 1);

        // Ongelezen laten is bewust: de volgende poll probeert het opnieuw, binnen de pogingenlimiet.
        graph.MarkedAsReadIds.Should().BeEmpty();
        graph.SentReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task RegistreerClassificatieFoutAsync_HerhaaldeFout_VerhoogtDePogingenteller()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService
        {
            StandToReturn = new EmailVerwerkingStand(
                VerwerkingId: 5, Status: nameof(EmailStatus.Fout), Pogingen: 1, AntwoordVerstuurd: false)
        };

        await EmailProcessorFunction.RegistreerClassificatieFoutAsync(
            Bericht(), graph, persistence, NullLogger.Instance);

        persistence.PogingVerhogingen.Should().Equal(5);
        persistence.Inserts.Should().BeEmpty();
        graph.MarkedAsReadIds.Should().BeEmpty();
    }

    /// <summary>
    /// Kern van de wachtrij-blokkade: de poll haalt de 10 oudste ongelezen berichten op. Blijft een
    /// structureel falend bericht ongelezen, dan houdt het samen met negen soortgenoten alle nieuwe
    /// post tegen — met elke poll opnieuw AI-kosten. Na de pogingenlimiet moet het bericht dus als
    /// gelezen worden gemarkeerd, met een definitieve foutstatus als spoor.
    /// </summary>
    [Fact]
    public async Task RegistreerClassificatieFoutAsync_NaMaxPogingen_MarkeertGelezenEnGeeftOp()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService
        {
            StandToReturn = new EmailVerwerkingStand(
                VerwerkingId: 6,
                Status: nameof(EmailStatus.Fout),
                Pogingen: EmailIdempotentie.MaxPogingen,
                AntwoordVerstuurd: false)
        };

        await EmailProcessorFunction.RegistreerClassificatieFoutAsync(
            Bericht("m-blokkeerder"), graph, persistence, NullLogger.Instance);

        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m-blokkeerder");
        persistence.FoutUpdates.Should().ContainSingle(u =>
            u.VerwerkingId == 6 && u.FoutMelding.Contains("Opgegeven"));
        persistence.PogingVerhogingen.Should().BeEmpty();
        persistence.Inserts.Should().BeEmpty();
        graph.SentReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task RegistreerClassificatieFoutAsync_AlDefinitiefAfgehandeld_RaaktDeRijNietAan()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService
        {
            StandToReturn = new EmailVerwerkingStand(
                VerwerkingId: 7,
                Status: nameof(EmailStatus.AntwoordVerstuurd),
                Pogingen: 1,
                AntwoordVerstuurd: true)
        };

        await EmailProcessorFunction.RegistreerClassificatieFoutAsync(
            Bericht("m-afgerond"), graph, persistence, NullLogger.Instance);

        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m-afgerond");
        persistence.FoutUpdates.Should().BeEmpty();
        persistence.PogingVerhogingen.Should().BeEmpty();
        persistence.Inserts.Should().BeEmpty();
    }
}
