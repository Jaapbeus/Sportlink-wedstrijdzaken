using AwesomeAssertions;
using FunctionApp.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

/// <summary>
/// Omhulseltest van <see cref="EmailReplyPolicyService"/> op de SQL Server-tier (#1760). De beslislogica
/// heeft zijn gevallen in <c>Planner.Shared.Tests</c> (<c>EmailReplyAfhandelingTests</c>); hier alleen wat
/// het omhulsel zelf doet: de eigen berichttypen vertalen, de opslag doorgeven en de Graph-statuscode
/// (<c>ODataError</c>) uitlezen (#1133).
/// </summary>
public class EmailReplyPolicyServiceTests
{
    private static Task<ReplyVerwerkingUitkomst> Draai(
        FakeEmailGraphService graph, RecordingEmailPersistenceService persistence, int verwerkingId = 1, bool reviewMode = false)
        => new EmailReplyPolicyService().HandelReplyFlowAfAsync(
            verwerkingId: verwerkingId,
            email: new InkomendBericht { MessageId = "m1", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test", ConversationId = "conv-1" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: reviewMode,
            reviewRecipient: null,
            graphService: graph,
            persistenceService: persistence,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            log: NullLogger.Instance);

    [Fact]
    public async Task Verstuurt_MetBerichtgegevensVanHetInkomendeBericht_EnLegtHetAntwoordVast()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();

        (await Draai(graph, persistence, verwerkingId: 10)).Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);

        graph.SentReplies.Should().ContainSingle(r =>
            r.To == "afzender@voorbeeld.test" && r.ConversationId == "conv-1" && r.Bcc == null && r.Bijlage == null);
        persistence.AntwoordUpdates.Should().ContainSingle(u => u.VerwerkingId == 10 && u.AntwoordEmail == "antwoord-body");
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m1");
    }

    [Fact]
    public async Task ReviewMode_SlaatVoorstelOp_ViaDeSqlServerOpslag()
    {
        var graph = new FakeEmailGraphService();
        var persistence = new RecordingEmailPersistenceService();

        (await Draai(graph, persistence, verwerkingId: 11, reviewMode: true)).Should().Be(ReplyVerwerkingUitkomst.AfgerondZonderAntwoord);

        persistence.VoorgesteldeAntwoorden.Should().ContainSingle(v => v.VerwerkingId == 11 && v.AntwoordEmail == "antwoord-body");
        graph.SentReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task GraphAfwijzing4xx_IsVerzendFout_EnWistDeIntentie()
    {
        var graph = new FakeEmailGraphService
        {
            ExceptionToThrowOnSendReply = new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 400 }
        };
        var persistence = new RecordingEmailPersistenceService();

        (await Draai(graph, persistence, verwerkingId: 12)).Should().Be(ReplyVerwerkingUitkomst.VerzendFout);

        persistence.VerzendPogingWissingen.Should().ContainSingle(id => id == 12);
        persistence.FoutUpdates.Should().ContainSingle(f => f.VerwerkingId == 12 && f.FoutMelding == "sanitized");
    }

    [Fact]
    public async Task Graph5xx_IsOnbekendeUitkomst_LaatIntentieStaan_EnZetReview()
    {
        var graph = new FakeEmailGraphService
        {
            ExceptionToThrowOnSendReply = new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 503 }
        };
        var persistence = new RecordingEmailPersistenceService();

        (await Draai(graph, persistence, verwerkingId: 13)).Should().Be(ReplyVerwerkingUitkomst.OnbekendeVerzendUitkomst);

        persistence.VerzendPogingWissingen.Should().BeEmpty();
        persistence.StatusUpdates.Should().ContainSingle(u => u.VerwerkingId == 13 && u.Status == EmailStatus.Review);
    }

    /// <summary>
    /// Een tweede poll ná een onbekende uitkomst mag géén tweede antwoord versturen (#1133): de stand die
    /// <see cref="Graph5xx_IsOnbekendeUitkomst_LaatIntentieStaan_EnZetReview"/> achterlaat wordt door
    /// <c>EmailIdempotentie.Bepaal</c> als onbeslist herkend.
    /// </summary>
    [Fact]
    public void NaOnbekendeUitkomst_ZietDeVolgendePollEenOnbesliteVerzendPoging_EnStuurtNietOpnieuw()
    {
        var stand = new EmailVerwerkingStand(
            VerwerkingId: 301, Status: nameof(EmailStatus.Review), Pogingen: 1,
            AntwoordVerstuurd: false, VerzendPogingOnbeslist: true);

        EmailIdempotentie.Bepaal(stand).Should().Be(VerwerkingsBesluit.OnbeslistNaVerzendPoging);
    }
}
