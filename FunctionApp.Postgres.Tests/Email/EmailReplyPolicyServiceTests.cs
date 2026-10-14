using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using FunctionApp.Postgres.Tests.Email.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Planner.Shared.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

/// <summary>
/// Omhulseltest van <see cref="EmailReplyPolicyService"/> op de Postgres-tier (#1760), zonder database:
/// de beslislogica heeft haar gevallen in <c>Planner.Shared.Tests</c> (<c>EmailReplyAfhandelingTests</c>);
/// hier alleen wat het omhulsel zelf doet: de eigen berichttypen vertalen, de opslag per connectiestring
/// doorgeven en de Graph-statuscode (<c>ODataError</c>) uitlezen (#1133). De echte opslag staat in
/// <c>EmailReplyPolicyServiceIntegrationTests</c>.
/// </summary>
public class EmailReplyPolicyServiceTests
{
    private static async Task<(ReplyVerwerkingUitkomst uitkomst, string? connectiestring)> Draai(
        FakeEmailGraphService graph, RecordingReplyPersistentie db, bool reviewMode = false)
    {
        string? gebruikteConnectiestring = null;
        var uitkomst = await new EmailReplyPolicyService(cs => { gebruikteConnectiestring = cs; return db; }).HandelReplyFlowAfAsync(
            connectionString: "verbinding-voor-test",
            verwerkingId: 7,
            email: new InkomendBericht { MessageId = "m7", Afzender = "afzender@voorbeeld.test", Onderwerp = "Test", ConversationId = "conv-7" },
            classificatie: new BerichtClassificatie { Type = VerzoekType.HerplanVerzoek },
            plannerResponseJson: "{}",
            reviewMode: reviewMode,
            reviewRecipient: null,
            graphService: graph,
            bouwTemplateAntwoordAsync: () => Task.FromResult(("antwoord-subject", "antwoord-body")),
            sanitizeFoutMelding: _ => "sanitized",
            log: NullLogger.Instance);
        return (uitkomst, gebruikteConnectiestring);
    }

    [Fact]
    public async Task Verstuurt_MetBerichtgegevensVanHetInkomendeBericht_EnLegtHetAntwoordVast_OpDeOpgegevenConnectiestring()
    {
        var graph = new FakeEmailGraphService();
        var db = new RecordingReplyPersistentie();

        var (uitkomst, cs) = await Draai(graph, db);

        uitkomst.Should().Be(ReplyVerwerkingUitkomst.AntwoordVerstuurd);
        cs.Should().Be("verbinding-voor-test");
        graph.SentReplies.Should().ContainSingle(r =>
            r.To == "afzender@voorbeeld.test" && r.ConversationId == "conv-7" && r.Bcc == null && r.Bijlage == null);
        db.AntwoordUpdates.Should().ContainSingle(u => u.VerwerkingId == 7 && u.AntwoordEmail == "antwoord-body");
        graph.MarkedAsReadIds.Should().ContainSingle(id => id == "m7");
    }

    [Fact]
    public async Task GraphAfwijzing4xx_IsVerzendFout_EnWistDeIntentie()
    {
        var graph = new FakeEmailGraphService
        {
            ExceptionToThrowOnSendReply = new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 400 }
        };
        var db = new RecordingReplyPersistentie();

        (await Draai(graph, db)).uitkomst.Should().Be(ReplyVerwerkingUitkomst.VerzendFout);

        db.VerzendPogingWissingen.Should().ContainSingle(id => id == 7);
        db.FoutUpdates.Should().ContainSingle(f => f.VerwerkingId == 7 && f.FoutMelding == "sanitized");
    }

    [Fact]
    public async Task Graph5xx_IsOnbekendeUitkomst_LaatIntentieStaan_EnZetReview()
    {
        var graph = new FakeEmailGraphService
        {
            ExceptionToThrowOnSendReply = new Microsoft.Graph.Models.ODataErrors.ODataError { ResponseStatusCode = 503 }
        };
        var db = new RecordingReplyPersistentie();

        (await Draai(graph, db)).uitkomst.Should().Be(ReplyVerwerkingUitkomst.OnbekendeVerzendUitkomst);

        db.VerzendPogingWissingen.Should().BeEmpty();
        db.StatusUpdates.Should().ContainSingle(u => u.VerwerkingId == 7 && u.Status == EmailStatus.Review);
    }
}
